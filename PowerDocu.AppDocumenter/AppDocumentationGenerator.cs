using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PowerDocu.Common;
using Rubjerg.Graphviz;

namespace PowerDocu.AppDocumenter
{
    public static class AppDocumentationGenerator
    {
        private static readonly ConcurrentDictionary<string, object> _appOutputLocks = new();

        /// <summary>
        /// Parses apps from the given file without generating documentation output.
        /// Returns the parsed apps and the resolved output path.
        /// </summary>
        public static (List<AppEntity> Apps, string Path) ParseApps(string filePath, string outputPath = null)
        {
            if (!File.Exists(filePath))
            {
                NotificationHelper.SendNotification("File not found: " + filePath);
                return (null, null);
            }

            string path = outputPath == null ? Path.GetDirectoryName(filePath) : $"{outputPath}/{Path.GetFileNameWithoutExtension(filePath)}";
            AppParser appParserFromZip = new AppParser(filePath);
            if (outputPath == null && appParserFromZip.packageType == AppParser.PackageType.SolutionPackage)
            {
                path += @"\Solution " + CharsetHelper.GetSafeName(Path.GetFileNameWithoutExtension(filePath));
            }
            List<AppEntity> apps = appParserFromZip.getApps();
            NotificationHelper.SendNotification($"AppParser: Parsed {apps.Count} app(s) from {filePath}.");
            return (apps, path);
        }

        /// <summary>
        /// Generates documentation output for pre-parsed apps using the DocumentationContext.
        /// </summary>
        public static void GenerateOutput(DocumentationContext context, string path)
        {
            if (context.Apps == null || !context.Config.documentApps) return;

            DateTime startDocGeneration = DateTime.Now;
            Parallel.ForEach(context.Apps,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                app =>
                {
                    string folderPath = path + CharsetHelper.GetSafeName(@"\AppDoc " + app.Name + @"\");
                    string appLockKey = Path.GetFullPath(folderPath).ToLowerInvariant();
                    object appOutputLock = _appOutputLocks.GetOrAdd(appLockKey, _ => new object());

                    lock (appOutputLock)
                    {
                        Directory.CreateDirectory(folderPath);
                        BuildScreenNavigationGraph(app, folderPath);
                        if (context.FullDocumentation)
                        {
                            AppDocumentationContent content = new AppDocumentationContent(app, path, context);
                            string wordTemplate = (!String.IsNullOrEmpty(context.Config.wordTemplate) && File.Exists(context.Config.wordTemplate))
                                ? context.Config.wordTemplate : null;
                            if (context.Config.outputFormat.Equals(OutputFormatHelper.Word) || context.Config.outputFormat.Equals(OutputFormatHelper.All))
                            {
                                NotificationHelper.SendNotification("Creating Word documentation");
                                if (wordTemplate == null)
                                {
                                    AppWordDocBuilder wordzip = new AppWordDocBuilder(content, null, context.Config.documentDefaultValuesCanvasApps, context.Config.documentDefaultValuesCanvasApps, context.Config.documentSampleData, context.Config.addTableOfContents);
                                }
                                else
                                {
                                    AppWordDocBuilder wordzip = new AppWordDocBuilder(content, wordTemplate, context.Config.documentChangesOnlyCanvasApps, context.Config.documentDefaultValuesCanvasApps, context.Config.documentSampleData, context.Config.addTableOfContents);
                                }
                            }
                            if (context.Config.outputFormat.Equals(OutputFormatHelper.Markdown) || context.Config.outputFormat.Equals(OutputFormatHelper.All))
                            {
                                NotificationHelper.SendNotification("Creating Markdown documentation");
                                AppMarkdownBuilder markdownFile = new AppMarkdownBuilder(content);
                            }
                            if (context.Config.outputFormat.Equals(OutputFormatHelper.Html) || context.Config.outputFormat.Equals(OutputFormatHelper.All))
                            {
                                NotificationHelper.SendNotification("Creating HTML documentation");
                                AppHtmlBuilder htmlFile = new AppHtmlBuilder(content, context.Config.documentChangesOnlyCanvasApps, context.Config.documentDefaultValuesCanvasApps, context.Config.documentSampleData);
                            }
                        }
                        context.Progress?.Increment("Apps");
                    }
                });
            DateTime endDocGeneration = DateTime.Now;
            NotificationHelper.SendNotification($"AppDocumenter: Generated documentation for {context.Apps.Count} app(s) in {(endDocGeneration - startDocGeneration).TotalSeconds} seconds.");
        }

        private static void BuildScreenNavigationGraph(AppEntity app, string folderPath)
        {
            RootGraph rootGraph = RootGraph.CreateNew(GraphType.Directed, CharsetHelper.GetSafeName(app.Name));
            Graph.IntroduceAttribute(rootGraph, "compound", "true");
            Graph.IntroduceAttribute(rootGraph, "fontname", "helvetica");
            Node.IntroduceAttribute(rootGraph, "shape", "rectangle");
            Node.IntroduceAttribute(rootGraph, "color", "");
            Node.IntroduceAttribute(rootGraph, "style", "");
            Node.IntroduceAttribute(rootGraph, "fillcolor", "");
            Node.IntroduceAttribute(rootGraph, "label", "");
            Node.IntroduceAttribute(rootGraph, "fontname", "helvetica");
            // Pre-compute screens list once — avoids O(n²) re-filtering inside the destination loop
            List<ControlEntity> screenControls = app.Controls.Where(o => o.Type == "screen").ToList();
            foreach (ControlEntity ce in app.ScreenNavigations.Keys)
            {
                List<string> destinations = app.ScreenNavigations[ce];
                if (destinations != null)
                {
                    foreach (string destination in destinations)
                    {
                        if (!destination.Contains("(") && !destination.Contains(","))
                        {
                            ControlEntity screen = ce.Screen();
                            if (screen != null)
                            {
                                Node source = rootGraph.GetOrAddNode(CharsetHelper.GetSafeName(screen.Name));
                                source.SetAttributeHtml("label", "<table border=\"0\"><tr><td>" + CharsetHelper.GetSafeName(ce.Screen().Name) + "</td></tr></table>");
                                Node dest = rootGraph.GetOrAddNode(CharsetHelper.GetSafeName(destination));
                                dest.SetAttributeHtml("label", "<table border=\"0\"><tr><td>" + CharsetHelper.GetSafeName(destination) + "</td></tr></table>");
                                rootGraph.GetOrAddEdge(source, dest, ce.Screen().Name + "-" + destination);
                            }
                            else
                            {
                                if (ce.Type == "appinfo")
                                {
                                    Node source = rootGraph.GetOrAddNode("App");
                                    source.SetAttributeHtml("label", "<table border=\"0\"><tr><td>App</td></tr></table>");
                                    source.SetAttribute("shape", "oval");
                                    Node dest = rootGraph.GetOrAddNode(CharsetHelper.GetSafeName(destination));
                                    dest.SetAttributeHtml("label", "<table border=\"0\"><tr><td>" + CharsetHelper.GetSafeName(destination) + "</td></tr></table>");
                                    rootGraph.GetOrAddEdge(source, dest, "App -" + destination);
                                }
                            }
                        }
                        else
                        {
                            foreach (ControlEntity screen in screenControls)
                            {
                                if (destination.Contains(screen.Name))
                                {
                                    Node source = rootGraph.GetOrAddNode(CharsetHelper.GetSafeName(ce.Screen().Name));
                                    source.SetAttributeHtml("label", "<table border=\"0\"><tr><td>" + CharsetHelper.GetSafeName(ce.Screen().Name) + "</td></tr></table>");
                                    Node dest = rootGraph.GetOrAddNode(CharsetHelper.GetSafeName(screen.Name));
                                    dest.SetAttributeHtml("label", "<table border=\"0\"><tr><td>" + CharsetHelper.GetSafeName(screen.Name) + "</td></tr></table>");
                                    rootGraph.GetOrAddEdge(source, dest, ce.Screen().Name + "-" + screen.Name);
                                }
                            }
                        }
                    }
                }
            }
            rootGraph.CreateLayout();
            rootGraph.ToPngFile(folderPath + "ScreenNavigation.png");
            rootGraph.ToSvgFile(folderPath + "ScreenNavigation.svg");
            rootGraph.ToDotFile(folderPath + "ScreenNavigation.dot");
        }

        /// <summary>
        /// Legacy method: parses and generates documentation in one step (used for standalone .msapp files).
        /// </summary>
        public static List<AppEntity> GenerateDocumentation(string filePath, bool fullDocumentation, ConfigHelper config, string outputPath = null)
        {
            var (apps, path) = ParseApps(filePath, outputPath);
            if (apps == null) return null;

            var context = new DocumentationContext
            {
                Apps = apps,
                Config = config,
                FullDocumentation = fullDocumentation
            };
            GenerateOutput(context, path);
            return apps;
        }
    }
}