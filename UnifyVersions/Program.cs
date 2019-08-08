using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace UnifyVersions
{
    public static class Program
    {
        private static readonly XNamespace MSBuildNamespace = "http://schemas.microsoft.com/developer/msbuild/2003";

        public static void Main(string[] args)
        {
            Console.WriteLine();

            if (args == null || args.Length != 2)
            {
                Console.WriteLine("Expected arguments: {path to root directory} {path to PackageVersions.props");
                return;
            }

            string rootDirectory = args[0];
            if (!Directory.Exists(rootDirectory))
            {
                Console.WriteLine("Root directory doesn't exist");
                return;
            }

            string packageVersionPropsFile = args[1];
            if (!File.Exists(packageVersionPropsFile) ||
                !StringComparer.OrdinalIgnoreCase.Equals("PackageVersions.props", Path.GetFileName(packageVersionPropsFile)))
            {
                Console.WriteLine("PackageVersions.props is not found.");
                return;
            }

            //
            // Find all the csproj files as well as all the NuGet packages referenced by the projects.
            //

            string[] projectFiles = Directory.GetFiles(rootDirectory, "*.csproj", SearchOption.AllDirectories);

            HashSet<Package> packages = GetAllPackages(projectFiles);

            //
            // Analyze and print the gaps between project files and PackageVersions.props.
            //

            PrintPackageVersionsPropertiesToAdd(packages);

            PrintPackageVersionsPropertiesToRemove(packageVersionPropsFile, packages);

            //
            // Analyze and print the TFM and RID to update.
            //

            PrintTfmAndRidToUpdate(projectFiles);

            //
            // Rewrite project files to remove explicit package versions.
            //

            RewriteProjectFiles(projectFiles);

            //
            // Format PackageVersions.props.
            //

            FormatPackageVersionsPropsFile(packageVersionPropsFile);

            //
            // Done.
            //

            Console.WriteLine("Completed.");
        }

        private static HashSet<Package> GetAllPackages(IEnumerable<string> projectFiles)
        {
            var packages = new HashSet<Package>(new PackageComparer());

            foreach (string projectFile in projectFiles)
            {
                var document = XDocument.Parse(File.ReadAllText(projectFile));

                var packageReferenceList = new List<XElement>();
                packageReferenceList.AddRange(document.Root.Elements("ItemGroup").SelectMany(p => p.Elements("PackageReference")));
                packageReferenceList.AddRange(document.Root.Elements("ItemGroup").SelectMany(p => p.Elements("DotNetCliToolReference")));

                foreach (var packageReference in packageReferenceList)
                {
                    string include = packageReference.Attribute("Include")?.Value ?? packageReference.Attribute("include")?.Value;

                    string version = packageReference.Attribute("Version")?.Value ?? packageReference.Attribute("version")?.Value;

                    if (string.IsNullOrEmpty(include))
                    {
                        Console.WriteLine($"Warning: invalid package reference: {packageReference.ToString()}");
                        continue;
                    }

                    //
                    // Deals with the special package Microsoft.AspNetCore.App.
                    //
                    if (string.Equals(include, "Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(version))
                        {
                            Console.WriteLine($"Warning: The package Microsoft.AspNetCore.App should not have a version.");
                        }
                        continue;
                    }

                    if (string.IsNullOrEmpty(version))
                    {
                        Console.WriteLine($"Warning: invalid package reference: {packageReference.ToString()}");
                        continue;
                    }

                    packages.Add(new Package(include, version));
                }
            }

            return packages;
        }

        private static void PrintPackageVersionsPropertiesToAdd(IEnumerable<Package> packages)
        {
            Console.WriteLine("Add the following to PackageVersions.props:");
            Console.WriteLine();

            var packagesToAdd = packages.Where(p => p.Version != p.MSBuildReferencedPackageVersionProperty)
                                        .OrderBy(p => p, new PackageComparer())
                                        .ToList();

            foreach (var packageToAdd in packagesToAdd)
            {
                Console.WriteLine(string.Format("<{0}>{1}</{0}>", packageToAdd.MSBuildPackageVersionProperty, packageToAdd.Version));
            }
        }

        private static void PrintPackageVersionsPropertiesToRemove(string packageVersionsPropsFile, IEnumerable<Package> packages)
        {
            Console.WriteLine("Remove the following from PackageVersions.props:");
            Console.WriteLine();

            var document = XDocument.Parse(File.ReadAllText(packageVersionsPropsFile));

            var packageVersions = document.Root.Elements(MSBuildNamespace + "PropertyGroup")
                                               .SelectMany(p => p.Elements())
                                               .Where(p => p.Name.LocalName.StartsWith("PackageVersion_"))
                                               .Select(p => p.Name.LocalName)
                                               .ToList();

            foreach (string packageVersion in packageVersions)
            {
                if (!packages.Any(p => p.MSBuildPackageVersionProperty == packageVersion))
                {
                    Console.WriteLine(packageVersion);
                }
            }
        }

        private static void PrintTfmAndRidToUpdate(IEnumerable<string> projectFiles)
        {
            Console.WriteLine("Update the following Tfm and Rid in the projects:");
            Console.WriteLine();

            foreach (string projectFile in projectFiles)
            {
                var document = XDocument.Parse(File.ReadAllText(projectFile));

                //
                // TargetFramework
                //

                var tfmElements = document.Root.Elements("PropertyGroup")
                                               .SelectMany(p => p.Elements())
                                               .Where(p => StringComparer.OrdinalIgnoreCase.Equals("TargetFramework", p.Name.LocalName))
                                               .Where(p => string.IsNullOrEmpty(p.Value) || !p.Value.StartsWith("$("))
                                               .ToList();

                foreach (var tfmElement in tfmElements)
                {
                    Console.WriteLine($"{tfmElement.ToString()} in {projectFile}");
                }

                //
                // RuntimeIdentifier
                //

                var ridElements = document.Root.Elements("PropertyGroup")
                                               .SelectMany(p => p.Elements())
                                               .Where(p => StringComparer.OrdinalIgnoreCase.Equals("RuntimeIdentifier", p.Name.LocalName))
                                               .Where(p => string.IsNullOrEmpty(p.Value) || !p.Value.StartsWith("$("))
                                               .ToList();

                foreach (var ridElement in ridElements)
                {
                    Console.WriteLine($"{ridElement.ToString()} in {projectFile}");
                }
            }
        }

        private static void RewriteProjectFiles(IEnumerable<string> projectFiles)
        {
            Console.WriteLine("Rewriting project files...");

            foreach (string projectFile in projectFiles)
            {
                var document = XDocument.Parse(File.ReadAllText(projectFile));

                var packageReferenceList = new List<XElement>();
                packageReferenceList.AddRange(document.Root.Elements("ItemGroup").SelectMany(p => p.Elements("PackageReference")));
                packageReferenceList.AddRange(document.Root.Elements("ItemGroup").SelectMany(p => p.Elements("DotNetCliToolReference")));

                foreach (var packageReference in packageReferenceList)
                {
                    string include = packageReference.Attribute("Include")?.Value ?? packageReference.Attribute("include")?.Value;

                    var attribute = packageReference.Attribute("Version") ?? packageReference.Attribute("version");

                    if (string.IsNullOrEmpty(include))
                    {
                        Console.WriteLine($"Warning: invalid package reference: {packageReference.ToString()}");
                        continue;
                    }

                    //
                    // Deals with the special package Microsoft.AspNetCore.App.
                    //
                    if (string.Equals(include, "Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(attribute?.Value))
                        {
                            Console.WriteLine($"Warning: The package Microsoft.AspNetCore.App should not have a version.");
                        }
                        continue;
                    }

                    if (string.IsNullOrEmpty(attribute?.Value))
                    {
                        Console.WriteLine($"Warning: invalid package reference: {packageReference.ToString()}");
                        continue;
                    }

                    var package = new Package(include, /* version doesn't matter here */ string.Empty);

                    attribute.Value = package.MSBuildReferencedPackageVersionProperty;
                }

                document.Save(projectFile);
            }

            Console.WriteLine("Rewriting project files completed.");
            Console.WriteLine();
        }

        private static void FormatPackageVersionsPropsFile(string packageVersionsPropsFile)
        {
            Console.WriteLine("Formatting PackageVersions.props...");

            var document = XDocument.Parse(File.ReadAllText(packageVersionsPropsFile));

            var propertyGroupElements = document.Root.Elements(MSBuildNamespace + "PropertyGroup").ToList();

            foreach (var propertyGroupElement in propertyGroupElements)
            {
                var sortedChildrenElements = propertyGroupElement.Elements().OrderBy(p => p.Name.ToString()).ToList();

                propertyGroupElement.ReplaceAll(sortedChildrenElements);
            }

            document.Save(packageVersionsPropsFile);

            // Workaround: Append a new line at the end to align with the team coding style.
            File.AppendAllText(packageVersionsPropsFile, Environment.NewLine, Encoding.UTF8);

            Console.WriteLine("Formatting PackageVersions.props completed.");
            Console.WriteLine();
        }

        private class Package
        {
            public Package(string id, string version)
            {
                Id = id ?? throw new ArgumentNullException(nameof(id));
                Version = version ?? throw new ArgumentNullException(nameof(version));
            }

            public string Id { get; }

            public string Version { get; }

            public string MSBuildPackageVersionProperty => "PackageVersion_" + Id.Replace(".", "_");

            public string MSBuildReferencedPackageVersionProperty => $"$({MSBuildPackageVersionProperty})";
        }

        private class PackageComparer : IComparer<Package>, IEqualityComparer<Package>
        {
            public int Compare(Package x, Package y)
            {
                int result = 0;

                result = StringComparer.OrdinalIgnoreCase.Compare(x.Id, y.Id);
                if (result != 0)
                {
                    return result;
                }

                result = StringComparer.OrdinalIgnoreCase.Compare(x.Version, y.Version);
                if (result != 0)
                {
                    return result;
                }

                return result;
            }

            public bool Equals(Package x, Package y)
            {
                return Compare(x, y) == 0;
            }

            public int GetHashCode(Package obj)
            {
                return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Id) ^
                       StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Version);
            }
        }
    }
}
