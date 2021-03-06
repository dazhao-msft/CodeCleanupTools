﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            PrintHelp();
            return -1;
        }

        string rootDirectory = args[0];

        if (!Directory.Exists(rootDirectory))
        {
            PrintHelp();
            return -1;
        }

        var files = Directory.GetFiles(rootDirectory, "*.csproj", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            SortPropertyGroups(file);

            SortProjectItems(file);
        }

        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"Usage: SortProjectItems.exe [<path-to-root-directory-of-csproj-files>]
       Sorts the ItemGroup contents of an MSBuild project file alphabetically.");
    }

    private static void SortPropertyGroups(string filePath)
    {
        XDocument document = XDocument.Load(filePath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        XNamespace msBuildNamespace = document.Root.GetDefaultNamespace();
        XName propertyGroupName = XName.Get("PropertyGroup", msBuildNamespace.NamespaceName);

        var propertyGroups = document.Root.Elements(propertyGroupName).ToArray();

        foreach (XElement propertyGroup in propertyGroups)
        {
            SortPropertyGroup(propertyGroup, filePath);
        }

        var originalBytes = File.ReadAllBytes(filePath);
        byte[] newBytes = null;

        using (var memoryStream = new MemoryStream())
        using (var textWriter = new StreamWriter(memoryStream, Encoding.UTF8))
        {
            document.Save(textWriter, SaveOptions.None);
            newBytes = memoryStream.ToArray();
        }

        if (!AreEqual(originalBytes, newBytes))
        {
            File.WriteAllBytes(filePath, newBytes);
        }
    }

    private static void SortPropertyGroup(XElement propertyGroup, string filePath)
    {
        var original = propertyGroup.Elements().ToArray();
        var sorted = original
            .OrderBy(i => i.Name.LocalName, SpecialPropertyNameComparer.Default)
            .ToArray();

        for (int i = 0; i < original.Length; i++)
        {
            original[i].ReplaceWith(sorted[i]);
        }

        // Check property consistency

        // <TargetFramework, RuntimeIdentifier>
        if (original.Any(p => p.Name.LocalName == "TargetFramework" && p.Value == "$(BizQAHostTargetFramework)"))
        {
            if (!original.Any(q => q.Name.LocalName == "RuntimeIdentifier" && q.Value == "$(BizQAHostRuntimeIdentifier)") &&
                !original.Any(q => q.Name.LocalName == "RuntimeIdentifiers" && q.Value.Contains("$(BizQAHostRuntimeIdentifier)")))
            {
                Console.WriteLine($"TargetFramework & RuntimeIdentifier|RuntimeIdentifiers don't match in {filePath}.");
            }
        }

        if (original.Any(p => p.Name.LocalName == "RuntimeIdentifier" && p.Value == "$(BizQAHostRuntimeIdentifier)") ||
            original.Any(q => q.Name.LocalName == "RuntimeIdentifiers" && q.Value.Contains("$(BizQAHostRuntimeIdentifier)")))
        {
            if (!original.Any(q => q.Name.LocalName == "TargetFramework" && q.Value == "$(BizQAHostTargetFramework)"))
            {
                Console.WriteLine($"TargetFramework & RuntimeIdentifier|RuntimeIdentifiers don't match in {filePath}.");
            }
        }

        // <TargetFramework, OutputType>
        if (original.Any(p => p.Name.LocalName == "TargetFramework" && p.Value == "$(BizQAHostTargetFramework)"))
        {
            if (!original.Any(q => q.Name.LocalName == "OutputType" && q.Value == "Exe"))
            {
                Console.WriteLine($"TargetFramework & OutputType don't match in {filePath}.");
            }
        }

        if (original.Any(p => p.Name.LocalName == "OutputType" && p.Value == "Exe"))
        {
            if (!original.Any(q => q.Name.LocalName == "TargetFramework" && q.Value == "$(BizQAHostTargetFramework)"))
            {
                Console.WriteLine($"TargetFramework & OutputType don't match in {filePath}.");
            }
        }

        // AssemblyName
        if (!original.Any(p => p.Name.LocalName == "AssemblyName"))
        {
            Console.WriteLine($"AssemblyName doesn't exist in {filePath}.");
        }

        // RootNamespace
        if (!original.Any(p => p.Name.LocalName == "RootNamespace"))
        {
            Console.WriteLine($"RootNamespace doesn't exist in {filePath}.");
        }
    }

    private static void SortProjectItems(string filePath)
    {
        XDocument document = XDocument.Load(filePath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        XNamespace msBuildNamespace = document.Root.GetDefaultNamespace();
        XName itemGroupName = XName.Get("ItemGroup", msBuildNamespace.NamespaceName);

        // only consider the top-level item groups, otherwise stuff inside Choose, Targets etc. will be broken
        var itemGroups = document.Root.Elements(itemGroupName).ToArray();

        var processedItemGroups = new List<XElement>();

        CombineCompatibleItemGroups(itemGroups, processedItemGroups);

        foreach (XElement itemGroup in processedItemGroups)
        {
            SortItemGroup(itemGroup);
        }

        var originalBytes = File.ReadAllBytes(filePath);
        byte[] newBytes = null;

        using (var memoryStream = new MemoryStream())
        using (var textWriter = new StreamWriter(memoryStream, Encoding.UTF8))
        {
            document.Save(textWriter, SaveOptions.None);
            newBytes = memoryStream.ToArray();
        }

        if (!AreEqual(originalBytes, newBytes))
        {
            File.WriteAllBytes(filePath, newBytes);
        }
    }

    private static bool AreEqual(byte[] left, byte[] right)
    {
        if (left == null)
        {
            return right == null;
        }

        if (right == null)
        {
            return false;
        }

        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void CombineCompatibleItemGroups(XElement[] itemGroups, List<XElement> processedItemGroups)
    {
        var itemTypeLookup = itemGroups.ToDictionary(i => i, i => GetItemTypesFromItemGroup(i));
        foreach (var itemGroup in itemGroups)
        {
            if (!itemGroup.HasElements)
            {
                RemoveItemGroup(itemGroup);
                continue;
            }

            var suitableExistingItemGroup = FindSuitableItemGroup(processedItemGroups, itemGroup, itemTypeLookup);
            if (suitableExistingItemGroup != null)
            {
                ReplantAllItems(from: itemGroup, to: suitableExistingItemGroup);

                RemoveItemGroup(itemGroup);
            }
            else
            {
                processedItemGroups.Add(itemGroup);
            }
        }
    }

    private static void RemoveItemGroup(XElement itemGroup)
    {
        var leadingTrivia = itemGroup.PreviousNode;
        if (leadingTrivia is XText)
        {
            leadingTrivia.Remove();
        }

        itemGroup.Remove();
    }

    private static void ReplantAllItems(XElement from, XElement to)
    {
        if (to.LastNode is XText)
        {
            to.LastNode.Remove();
        }

        var fromNodes = from.Nodes().ToArray();
        from.RemoveNodes();
        foreach (var element in fromNodes)
        {
            to.Add(element);
        }
    }

    private static XElement FindSuitableItemGroup(
        List<XElement> existingItemGroups,
        XElement itemGroup,
        Dictionary<XElement, HashSet<string>> itemTypeLookup)
    {
        foreach (var existing in existingItemGroups)
        {
            var itemTypesInExisting = itemTypeLookup[existing];
            var itemTypesInCurrent = itemTypeLookup[itemGroup];
            if (itemTypesInCurrent.IsSubsetOf(itemTypesInExisting) && AreItemGroupsMergeable(itemGroup, existing))
            {
                return existing;
            }
        }

        return null;
    }

    private static bool AreItemGroupsMergeable(XElement left, XElement right)
    {
        if (!AttributeMissingOrSame(left, right, "Label"))
        {
            return false;
        }

        if (!AttributeMissingOrSame(left, right, "Condition"))
        {
            return false;
        }

        return true;
    }

    private static bool AttributeMissingOrSame(XElement left, XElement right, string attributeName)
    {
        var leftAttribute = left.Attribute(attributeName);
        var rightAttribute = right.Attribute(attributeName);
        if (leftAttribute == null && rightAttribute == null)
        {
            return true;
        }
        else if (leftAttribute != null && rightAttribute != null)
        {
            return leftAttribute.Value == rightAttribute.Value;
        }

        return false;
    }

    private static HashSet<string> GetItemTypesFromItemGroup(XElement itemGroup)
    {
        var set = new HashSet<string>();
        foreach (var item in itemGroup.Elements())
        {
            set.Add(item.Name.LocalName);
        }

        return set;
    }

    private static void SortItemGroup(XElement itemGroup)
    {
        var original = itemGroup.Elements().ToArray();
        var sorted = original
            .OrderBy(i => i.Name.LocalName)
            .ThenBy(i => (i.Attribute("Include") ?? i.Attribute("Update") ?? i.Attribute("Remove")).Value)
            .ToArray();

        for (int i = 0; i < original.Length; i++)
        {
            original[i].ReplaceWith(sorted[i]);
        }
    }

    private class SpecialPropertyNameComparer : StringComparer
    {
        public static readonly SpecialPropertyNameComparer Default = new SpecialPropertyNameComparer();

        private static readonly Dictionary<string, int> SpecialPropertyNames = new Dictionary<string, int>()
        {
            { "TargetFramework", 0 },
            { "TargetFrameworks", 0 },
            { "RuntimeIdentifier", 1 },
            { "RuntimeIdentifiers", 1 },
            { "TargetLatestRuntimePatch", 2 },
            { "CopyLocalLockFileAssemblies", 3 },
            { "OutputType", 4 },
            { "AssemblyName", 5 },
            { "RootNamespace", 6 },
            { "IsPackable", 7 },
        };

        public override int Compare(string x, string y)
        {
            bool isXSpecial = SpecialPropertyNames.ContainsKey(x);
            bool isYSpecial = SpecialPropertyNames.ContainsKey(y);

            if (isXSpecial && isYSpecial)
            {
                return SpecialPropertyNames[x] - SpecialPropertyNames[y];
            }
            else if (isXSpecial)
            {
                return -1;
            }
            else if (isYSpecial)
            {
                return 1;
            }
            else
            {
                return Ordinal.Compare(x, y);
            }
        }

        public override bool Equals(string x, string y)
        {
            return Ordinal.Equals(x, y);
        }

        public override int GetHashCode(string obj)
        {
            return Ordinal.GetHashCode(obj);
        }
    }
}
