﻿using Serenity.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Serenity.Web
{
    internal static class BundleUtils
    {
        internal static ConcurrentDictionary<string, string> expandVersion;

        static BundleUtils()
        {
            expandVersion = new ConcurrentDictionary<string, string>();
        }

        public static void ClearVersionCache()
        {
            expandVersion.Clear();
        }

        public static string GetLatestVersion(string path, string mask)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));

            if (mask.IsNullOrEmpty())
                return null;

            var idx = mask.IndexOf("*", StringComparison.Ordinal);
            if (idx <= 0)
                throw new ArgumentOutOfRangeException(nameof(mask));

            var before = mask.Substring(0, idx);
            var after = mask[(idx + 1)..];
            var extension = Path.GetExtension(mask);

            var files = Directory.GetFiles(path, mask)
                .Select(x =>
                {
                    var filename = Path.GetFileName(x);
                    return filename.Substring(before.Length, filename.Length - before.Length - after.Length);
                })
                .Where(s =>
                {
                    if (s.Length < 0)
                        return false;
                    return s.Split('.').All(x => int.TryParse(x, out int y));
                })
                .ToArray();

            if (!files.Any())
                return null;

            Array.Sort(files, (x, y) =>
            {
                var px = x.Split('.');
                var py = y.Split('.');

                for (var i = 0; i < Math.Min(px.Length, py.Length); i++)
                {
                    var c = int.Parse(px[i], CultureInfo.InvariantCulture)
                        .CompareTo(int.Parse(py[i], CultureInfo.InvariantCulture));
                    if (c != 0)
                        return c;
                }

                return px.Length.CompareTo(py.Length);
            });

            return files.Last();
        }

        public static string ExpandVersionVariable(string webRootPath, string scriptUrl)
        {
            if (scriptUrl.IsNullOrEmpty())
                return scriptUrl;

            var tpl = "{version}";
            var idx = scriptUrl.IndexOf(tpl, StringComparison.OrdinalIgnoreCase);

            if (idx < 0)
                return scriptUrl;

            if (expandVersion.TryGetValue(scriptUrl, out string result))
                return result;

            var before = scriptUrl.Substring(0, idx);
            var after = scriptUrl[(idx + tpl.Length)..];
            var extension = Path.GetExtension(scriptUrl);

            var path = PathHelper.SecureCombine(webRootPath, before.StartsWith("~/", StringComparison.Ordinal) ? before[2..] : before);
            path = Path.GetDirectoryName(path);

            var beforeName = Path.GetFileName(before.Replace('/', Path.DirectorySeparatorChar));

            var latest = GetLatestVersion(path, beforeName + "*" + extension.Replace('/', Path.DirectorySeparatorChar));
            if (latest == null)
            {
                expandVersion[scriptUrl] = scriptUrl;
                return scriptUrl;
            }

            result = before + latest + after;
            expandVersion[scriptUrl] = result;
            return result;
        }

        public static string DoReplacements(string scriptUrl, Dictionary<string, object> replacements)
        {
            int idx = 0;
            do
            {
                idx = scriptUrl.IndexOf('{', idx);
                if (idx < 0)
                    break;

                var end = scriptUrl.IndexOf('}', idx + 1);
                if (end < 0)
                    break;

                var key = scriptUrl.Substring(idx + 1, end - idx - 1);
                if (string.IsNullOrEmpty(key))
                    return null;

                if (key == "version")
                {
                    idx = end + 1;
                    continue;
                }

                bool falsey = key.StartsWith("!", StringComparison.Ordinal);
                if (falsey)
                    key = key[1..];

                if (string.IsNullOrEmpty(key))
                    return null;

                if (replacements == null ||
                    !replacements.TryGetValue(key, out object value))
                {
                    value = null;
                }

                string replace;
                if (falsey)
                {
                    if (value is bool b && b == true)
                        return null;

                    if (value != null && (value is not bool b2 || (b2 == true)))
                        return null;

                    replace = "";
                }
                else
                {
                    if (value == null)
                        return null;

                    if (value is bool b && b == false)
                        return null;

                    if (value is bool b2 && b2 == true)
                        replace = "";
                    else
                        replace = value.ToString();
                }

                scriptUrl = scriptUrl.Substring(0, idx) + replace + scriptUrl[(end + 1)..];
                idx = end + 1 + (replace.Length - key.Length - (falsey ? 1 : 0));
            }
            while (idx < scriptUrl.Length - 1);

            return scriptUrl;
        }

        public static Dictionary<string, List<string>> ExpandBundleIncludes(Dictionary<string, string[]> bundles,
            string bundlePrefix, string bundleType)
        {
            var recursionCheck = new HashSet<string>();

            var bundleIncludes = new Dictionary<string, List<string>>();
            List<string> listBundleIncludes(string bundleKey)
            {
                var includes = new List<string>();

                if (!bundles.TryGetValue(bundleKey, out string[] sourceFiles) ||
                    sourceFiles == null ||
                    sourceFiles.Length == 0)
                    return includes;

                foreach (var sourceFile in sourceFiles)
                {
                    if (sourceFile.IsNullOrEmpty())
                        continue;

                    if (sourceFile.StartsWith(bundlePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var subBundleKey = sourceFile[17..];
                        if (recursionCheck != null)
                        {
                            if (recursionCheck.Contains(subBundleKey) || recursionCheck.Count > 100)
                                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture,
                                    "Caught infinite recursion with {1} bundles '{0}'!",
                                        string.Join(", ", recursionCheck), bundleType));
                        }
                        else
                            recursionCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        recursionCheck.Add(subBundleKey);
                        try
                        {
                            includes.AddRange(listBundleIncludes(subBundleKey));
                        }
                        finally
                        {
                            recursionCheck.Remove(subBundleKey);
                        }
                    }
                    else
                        includes.Add(sourceFile);
                }

                return includes;
            }

            foreach (var bundleKey in bundles.Keys)
            {
                var includes = listBundleIncludes(bundleKey);
                bundleIncludes[bundleKey] = includes;
            }

            return bundleIncludes;
        }
    }
}