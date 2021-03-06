﻿using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Serenity.Abstractions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Serenity.Web
{
    public class CssBundleManager : ICssBundleManager
    {
        private readonly object sync = new object();

        private bool isEnabled;
        private Dictionary<string, string> bundleKeyBySourceUrl;
        private Dictionary<string, HashSet<string>> bundleKeysBySourceUrl;
        private HashSet<string> bundleKeys;
        private Dictionary<string, List<string>> bundleIncludes;

        private const string errorLines = "\r\n/*\r\n!!!ERROR: {0}!!!\r\n*/\r\n";
        private readonly IDynamicScriptManager scriptManager;
        private readonly IWebHostEnvironment hostEnvironment;
        private readonly IHttpContextAccessor contextAccessor;
        private readonly IExceptionLogger logger;
        private readonly CssBundlingOptions options;

        [ThreadStatic]
        private static HashSet<string> recursionCheck;

        public CssBundleManager(IOptions<CssBundlingOptions> options, IDynamicScriptManager scriptManager, IWebHostEnvironment hostEnvironment,
            IHttpContextAccessor contextAccessor = null, IExceptionLogger logger = null)
        {
            this.options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
            this.scriptManager = scriptManager ?? throw new ArgumentNullException(nameof(scriptManager));
            this.hostEnvironment = hostEnvironment ?? throw new ArgumentNullException(nameof(hostEnvironment));
            this.contextAccessor = contextAccessor;
            this.logger = logger;

            Reset();
            scriptManager.ScriptChanged += name =>
            {
                HashSet<string> bundleKeys;
                lock (sync)
                {
                    if (bundleKeysBySourceUrl == null ||
                        !bundleKeysBySourceUrl.TryGetValue("dynamic://" + name, out bundleKeys))
                        bundleKeys = null;
                    {
                    }
                }

                if (bundleKeys != null)
                    foreach (var bundleKey in bundleKeys)
                        scriptManager.Changed("Bundle." + bundleKey);
            };
        }

        public void Reset()
        {
            lock (sync)
            {
                isEnabled = false;
                var settings = options.Value;
                var bundles = settings.Bundles ?? new Dictionary<string, string[]>();

                bundles = bundles.Keys.ToDictionary(k => k,
                    k => (bundles[k] ?? Array.Empty<string>())
                        .Select(u => BundleUtils.DoReplacements(u, settings.Replacements))
                        .Where(u => !string.IsNullOrEmpty(u))
                        .ToArray());

                bundleIncludes = BundleUtils.ExpandBundleIncludes(bundles, "dynamic://CssBundle.", "css");

                if (bundles.Count == 0 ||
                    settings.Enabled != true)
                {
                    bundleKeyBySourceUrl = null;
                    bundleKeysBySourceUrl = null;
                    bundleKeys = null;
                    return;
                }

                bundleKeyBySourceUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bundleKeysBySourceUrl = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                bundleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                bool minimize = settings.Minimize == true;
                var noMinimize = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (settings.NoMinimize != null)
                    noMinimize.AddRange(settings.NoMinimize);

                foreach (var pair in bundles)
                {
                    var sourceFiles = pair.Value;
                    if (sourceFiles == null ||
                        sourceFiles.Length == 0)
                        continue;

                    var bundleKey = pair.Key;
                    var bundleName = "CssBundle." + bundleKey;
                    var bundleParts = new List<Func<string>>();
                    var scriptNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    void registerInBundle(string sourceFile)
                    {
                        if (bundleKey.IndexOf('/', StringComparison.Ordinal) < 0 && !bundleKeyBySourceUrl.ContainsKey(sourceFile))
                            bundleKeyBySourceUrl[sourceFile] = bundleKey;

                        if (!bundleKeysBySourceUrl.TryGetValue(sourceFile, out HashSet<string> bundleKeys))
                        {
                            bundleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            bundleKeysBySourceUrl[sourceFile] = new HashSet<string>();
                        }

                        bundleKeys.Add(bundleKey);
                    }

                    foreach (var sourceFile in sourceFiles)
                    {
                        if (sourceFile.IsNullOrEmpty())
                            continue;

                        if (sourceFile.StartsWith("dynamic://", StringComparison.OrdinalIgnoreCase))
                        {
                            registerInBundle(sourceFile);

                            var scriptName = sourceFile[10..];
                            scriptNames.Add(scriptName);
                            bundleParts.Add(() =>
                            {
                                if (recursionCheck != null)
                                {
                                    if (recursionCheck.Contains(scriptName) || recursionCheck.Count > 100)
                                        return string.Format(CultureInfo.CurrentCulture, errorLines,
                                            string.Format(CultureInfo.CurrentCulture, "Caught infinite recursion with dynamic scripts '{0}'!",
                                                string.Join(", ", recursionCheck)));
                                }
                                else
                                    recursionCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                                recursionCheck.Add(scriptName);
                                try
                                {
                                    var code = scriptManager.GetScriptText(scriptName);
                                    if (code == null)
                                        return string.Format(CultureInfo.CurrentCulture, errorLines,
                                            string.Format(CultureInfo.CurrentCulture, "Dynamic script with name '{0}' is not found!", scriptName));

                                    if (minimize &&
                                        !scriptName.StartsWith("Bundle.", StringComparison.OrdinalIgnoreCase))
                                    {
                                        try
                                        {
                                            var result = NUglify.Uglify.Css(code);
                                            if (!result.HasErrors)
                                                code = result.Code;
                                        }
                                        catch (Exception ex)
                                        {
                                            ex.Log(logger);
                                        }
                                    }

                                    var scriptUrl = VirtualPathUtility.ToAbsolute(contextAccessor, "~/DynJS.axd/" + scriptName);
                                    return RewriteUrlsToAbsolute(scriptUrl, code);
                                }
                                finally
                                {
                                    recursionCheck.Remove(scriptName);
                                }
                            });

                            continue;
                        }

                        string sourceUrl = BundleUtils.ExpandVersionVariable(hostEnvironment.WebRootPath, sourceFile);
                        sourceUrl = VirtualPathUtility.ToAbsolute(contextAccessor, sourceUrl);
                        var rootUrl = VirtualPathUtility.ToAbsolute(contextAccessor, "~/");

                        if (sourceUrl.IsNullOrEmpty() || !sourceUrl.StartsWith(rootUrl, StringComparison.Ordinal))
                            continue;

                        registerInBundle(sourceUrl);

                        bundleParts.Add(() =>
                        {
                            var sourcePath = PathHelper.SecureCombine(hostEnvironment.WebRootPath, 
                                sourceUrl[rootUrl.Length..]);
                            if (!File.Exists(sourcePath))
                                return string.Format(CultureInfo.CurrentCulture, errorLines, 
                                    string.Format(CultureInfo.CurrentCulture, "File {0} is not found!", sourcePath));

                            string code = null;

                            if (minimize &&
                                !noMinimize.Contains(sourceFile) &&
                                !sourceFile.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase))
                            {
                                if (settings.UseMinCSS == true)
                                {
                                    var minPath = Path.ChangeExtension(sourcePath, ".min.css");
                                    if (File.Exists(minPath))
                                    {
                                        sourcePath = minPath;
                                        using StreamReader sr = new StreamReader(File.OpenRead(sourcePath));
                                        code = sr.ReadToEnd();
                                    }
                                }

                                if (code == null)
                                {
                                    using (StreamReader sr = new StreamReader(File.OpenRead(sourcePath)))
                                        code = sr.ReadToEnd();

                                    try
                                    {
                                        var result = NUglify.Uglify.Css(code, new NUglify.Css.CssSettings
                                        {
                                            LineBreakThreshold = 1000
                                        });
                                        if (!result.HasErrors)
                                            code = result.Code;
                                    }
                                    catch (Exception ex)
                                    {
                                        ex.Log(logger);
                                    }
                                }
                            }
                            else
                            {
                                using StreamReader sr = new StreamReader(File.OpenRead(sourcePath));
                                code = sr.ReadToEnd();
                            }

                            code = RewriteUrlsToAbsolute(sourceUrl, code);
                            return code;
                        });
                    }

                    var bundle = new ConcatenatedScript(bundleParts, "\n\n", checkRights: (permissions, localizer) =>
                    {
                        foreach (var scriptName in scriptNames)
                        {
                            if (recursionCheck != null)
                            {
                                if (recursionCheck.Contains(scriptName) || recursionCheck.Count > 100)
                                    throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture,
                                        "Caught infinite recursion with dynamic scripts '{0}'!",
                                            string.Join(", ", recursionCheck)));
                            }
                            else
                                recursionCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                            recursionCheck.Add(scriptName);
                            try
                            {
                                scriptManager.CheckScriptRights(scriptName);
                            }
                            finally
                            {
                                recursionCheck.Remove(scriptName);
                            }
                        }
                    });

                    scriptManager.Register(bundleName, bundle);
                    bundleKeys.Add(bundleKey);
                }

                isEnabled = true;
            }
        }

        public bool IsEnabled
        {
            get
            {
                lock (sync)
                    return isEnabled;
            }
        }

        public void CssChanged()
        {
            BundleUtils.ClearVersionCache();

            lock (sync)
            {
                if (bundleKeys == null)
                    return;

                foreach (var bundleKey in bundleKeys)
                    scriptManager.Changed("CssBundle." + bundleKey);

                Reset();
            }
        }

        public IEnumerable<string> GetBundleIncludes(string bundleKey)
        {
            lock (sync)
            {
                var bi = bundleIncludes;
                if (bi != null && bi.TryGetValue(bundleKey, out List<string> includes) && includes != null)
                    return includes;

                return Array.Empty<string>();
            }
        }

        private static string UrlToAbsolute(string absolutePath, string url, string prefix, string suffix)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                (url.Contains("://", StringComparison.Ordinal)))
                return prefix + url + suffix;

            url = url.TrimStart();
            if (string.IsNullOrWhiteSpace(url) || url[0] == '/')
                return prefix + url + suffix;

            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return prefix + url + suffix;

            var question = url.IndexOf('?', StringComparison.Ordinal);
            if (question >= 0)
            {
                return prefix + new Uri("x:" + absolutePath + url.Substring(0, question)).AbsolutePath[2..]
                    + url[question..] + suffix;
            }

            return prefix + new Uri("x:" + absolutePath + url).AbsolutePath[2..] + suffix;
        }

        private string RewriteUrlsToAbsolute(string virtualPath, string content)
        {
            if (string.IsNullOrEmpty(content) ||
                string.IsNullOrEmpty(virtualPath))
                return content;

            var absolutePath = Path.GetDirectoryName(virtualPath)
                .Replace('\\', '/');

            if (string.IsNullOrWhiteSpace(absolutePath))
                return content;

            if (absolutePath.StartsWith("~", StringComparison.Ordinal))
                absolutePath = VirtualPathUtility.ToAbsolute(contextAccessor, absolutePath);

            if (!absolutePath.EndsWith("/", StringComparison.OrdinalIgnoreCase))
                absolutePath += "/";

            var regex = new Regex("url\\((?<prefix>['\"]?)(?<url>[^)]+?)(?<suffix>['\"]?)\\)");
            return regex.Replace(content, (Match match) => "url(" +
                UrlToAbsolute(absolutePath, match.Groups["url"].Value,
                    match.Groups["prefix"].Value,
                    match.Groups["suffix"].Value) + ")");
        }

        public string GetCssBundle(string cssUrl)
        {
            Dictionary<string, string> bySrcUrl;
            lock (sync)
            {
                bySrcUrl = bundleKeyBySourceUrl;
            }

            string bundleKey;

            if (cssUrl != null && cssUrl.StartsWith("dynamic://",
                StringComparison.OrdinalIgnoreCase))
            {
                var scriptName = cssUrl[10..];
                if (bySrcUrl == null || !bySrcUrl.TryGetValue(cssUrl, out bundleKey))
                {
                    cssUrl = scriptManager.GetScriptInclude(scriptName, ".css");
                    return VirtualPathUtility.ToAbsolute(contextAccessor, "~/DynJS.axd/" + cssUrl);
                }
            }
            else
            {
                cssUrl = BundleUtils.ExpandVersionVariable(hostEnvironment.WebRootPath, cssUrl);
                cssUrl = VirtualPathUtility.ToAbsolute(contextAccessor, cssUrl);

                if (bySrcUrl == null ||
                    !bySrcUrl.TryGetValue(cssUrl, out bundleKey))
                    return cssUrl;
            }

            string include = scriptManager.GetScriptInclude("CssBundle." + bundleKey, ".css");
            return VirtualPathUtility.ToAbsolute(contextAccessor, "~/DynJS.axd/" + include);
        }
    }
}