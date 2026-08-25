using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PowerDocu.Common;
using Rubjerg.Graphviz;

namespace PowerDocu.DesktopFlowDocumenter
{
    internal enum TreeNodeKind { Action, ControlFlow }

    internal class FlowTreeNode
    {
        public TreeNodeKind Kind;
        public RobinActionStep ActionStep;
        public RobinControlFlowBlock ControlBlock;
        public List<FlowTreeNode> Children = new List<FlowTreeNode>();
        public List<FlowTreeNode> ElseChildren = new List<FlowTreeNode>();
    }

    public class GraphBuilder
    {
        private readonly string folderPath;
        private int clusterCounter;

        // Data for graph generation (either main flow or a subflow)
        private readonly string displayName;
        private readonly string startNodeLabel;
        private readonly string filePrefix;
        private readonly List<RobinActionStep> actionSteps;
        private readonly List<RobinControlFlowBlock> controlFlowBlocks;
        private readonly string robinScript;

        public GraphBuilder(DesktopFlowEntity flow, string path)
        {
            folderPath = path;
            displayName = flow.GetDisplayName();
            startNodeLabel = "Desktop Flow: " + flow.GetDisplayName();
            filePrefix = "desktopflow";
            actionSteps = flow.ActionSteps;
            controlFlowBlocks = flow.ControlFlowBlocks;
            robinScript = flow.RobinScript;
        }

        public GraphBuilder(DesktopFlowSubflow subflow, string flowDisplayName, string path)
        {
            folderPath = path;
            displayName = subflow.Name;
            startNodeLabel = "Subflow: " + subflow.Name + (subflow.IsGlobal ? " (Global)" : "");
            filePrefix = "desktopflow-subflow-" + CharsetHelper.GetSafeName(subflow.Name);
            actionSteps = subflow.ActionSteps;
            controlFlowBlocks = subflow.ControlFlowBlocks;
            robinScript = subflow.RobinScript;
        }

        public void buildTopLevelGraph()
        {
            buildGraph(false);
        }

        public void buildDetailedGraph()
        {
            buildGraph(true);
        }

        private void buildGraph(bool showSubactions)
        {
            var rootNodes = reconstructTree();
            if (rootNodes.Count == 0) return;

            clusterCounter = 0;

            RootGraph rootGraph = RootGraph.CreateNew(GraphType.Directed, CharsetHelper.GetSafeName(displayName));
            Graph.IntroduceAttribute(rootGraph, "rankdir", "TB");
            Graph.IntroduceAttribute(rootGraph, "compound", "true");
            Graph.IntroduceAttribute(rootGraph, "fontname", "helvetica");
            Graph.IntroduceAttribute(rootGraph, "clusterrank", "local");
            Graph.IntroduceAttribute(rootGraph, "nodesep", "0.4");
            Graph.IntroduceAttribute(rootGraph, "ranksep", "0.3");

            SubGraph.IntroduceAttribute(rootGraph, "style", "filled");
            SubGraph.IntroduceAttribute(rootGraph, "color", "black");
            SubGraph.IntroduceAttribute(rootGraph, "fillcolor", "lightgray");
            SubGraph.IntroduceAttribute(rootGraph, "penwidth", "1");

            Node.IntroduceAttribute(rootGraph, "shape", "plain");
            Node.IntroduceAttribute(rootGraph, "color", "");
            Node.IntroduceAttribute(rootGraph, "style", "");
            Node.IntroduceAttribute(rootGraph, "fillcolor", "");
            Node.IntroduceAttribute(rootGraph, "label", "");
            Node.IntroduceAttribute(rootGraph, "fontname", "helvetica");
            Edge.IntroduceAttribute(rootGraph, "label", "");

            // Start node
            Node startNode = rootGraph.GetOrAddNode("__start__");
            startNode.SetAttribute("shape", "plaintext");
            startNode.SetAttribute("margin", "0");
            startNode.SetAttributeHtml("label", generateCardHtml(
                DesktopFlowGraphColours.TriggerColour,
                HtmlEncode(startNodeLabel)));

            Node prevNode = startNode;

            foreach (var treeNode in rootNodes)
            {
                if (!showSubactions && treeNode.Kind == TreeNodeKind.ControlFlow)
                {
                    prevNode = addCollapsedControlNode(rootGraph, treeNode, prevNode);
                }
                else
                {
                    prevNode = addNodeToGraph(rootGraph, treeNode, prevNode, null, showSubactions);
                }
            }

            try
            {
                rootGraph.ToDotFile(folderPath + filePrefix + ".dot");
                rootGraph.CreateLayout();

                NotificationHelper.SendNotification("  - Created Graph " + folderPath + generateImageFiles(rootGraph, showSubactions) + ".png");
            }
            catch (Exception ex)
            {
                NotificationHelper.SendNotification("  - Failed to create Desktop Flow graph for " + displayName + ": " + ex.Message);
            }
        }

        private List<FlowTreeNode> reconstructTree()
        {
            var rootNodes = new List<FlowTreeNode>();
            if (string.IsNullOrEmpty(robinScript) || actionSteps.Count == 0)
                return rootNodes;

            string[] lines = robinScript.Split('\n');

            // Build lookup: StartLine → ControlFlowBlock
            var cfByLine = new Dictionary<int, RobinControlFlowBlock>();
            foreach (var block in controlFlowBlocks)
                cfByLine[block.StartLine] = block;

            // Queue of action steps in order
            var actionQueue = new Queue<RobinActionStep>(actionSteps.OrderBy(s => s.Order));

            // Stack: (containerNode or null for root, children list to add to)
            var stack = new Stack<(FlowTreeNode container, List<FlowTreeNode> children)>();
            stack.Push((null, rootNodes));

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r').Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Skip non-structural lines
                if (line.StartsWith("@@") || line.StartsWith("@") || line.StartsWith("IMPORT ")
                    || line.StartsWith("#") || line.StartsWith("LABEL ") || line.StartsWith("GOTO "))
                    continue;

                // Skip FUNCTION/END FUNCTION boundaries (handled by parser, not in subflow script)
                if (line.StartsWith("FUNCTION ") || line == "END FUNCTION")
                    continue;

                if (line == "END")
                {
                    if (stack.Count > 1) stack.Pop();
                    continue;
                }

                if (line == "ELSE")
                {
                    // Switch current IF container's output to ElseChildren
                    if (stack.Count > 1)
                    {
                        var (container, _) = stack.Pop();
                        if (container != null && container.Kind == TreeNodeKind.ControlFlow
                            && container.ControlBlock?.Type == "IF")
                        {
                            stack.Push((container, container.ElseChildren));
                        }
                        else
                        {
                            // Fallback: put it back
                            stack.Push((container, container?.Children ?? rootNodes));
                        }
                    }
                    continue;
                }

                // Check if this line is a control flow block
                if (cfByLine.TryGetValue(i, out var cfBlock))
                {
                    var containerNode = new FlowTreeNode
                    {
                        Kind = TreeNodeKind.ControlFlow,
                        ControlBlock = cfBlock
                    };
                    stack.Peek().children.Add(containerNode);
                    stack.Push((containerNode, containerNode.Children));
                    continue;
                }

                // Check if this line produces an action step
                if (actionQueue.Count > 0)
                {
                    string testLine = line;
                    if (testLine.StartsWith("DISABLE "))
                        testLine = testLine.Substring(8);

                    bool isActionLine = testLine.StartsWith("SET ")
                        || testLine.StartsWith("WAIT ")
                        || testLine.StartsWith("CALL ")
                        || testLine == "EXIT FUNCTION"
                        || testLine.StartsWith("Variables.")
                        || Regex.IsMatch(testLine, @"^[A-Za-z]+\.[A-Za-z]");

                    if (isActionLine)
                    {
                        var step = actionQueue.Dequeue();
                        var actionNode = new FlowTreeNode
                        {
                            Kind = TreeNodeKind.Action,
                            ActionStep = step
                        };
                        stack.Peek().children.Add(actionNode);
                    }
                }
            }

            return rootNodes;
        }

        private Node addNodeToGraph(RootGraph rootGraph, FlowTreeNode treeNode, Node prevNode, SubGraph parentCluster, bool showSubactions)
        {
            if (treeNode.Kind == TreeNodeKind.Action)
            {
                return addActionNode(rootGraph, treeNode.ActionStep, prevNode, parentCluster);
            }
            else
            {
                return addControlFlowCluster(rootGraph, treeNode, prevNode, parentCluster, showSubactions);
            }
        }

        private Node addActionNode(RootGraph rootGraph, RobinActionStep step, Node prevNode, SubGraph parentCluster)
        {
            string nodeId = "action_" + step.Order;
            Node actionNode = rootGraph.GetOrAddNode(nodeId);
            actionNode.SetAttribute("shape", "plaintext");
            actionNode.SetAttribute("margin", "0");
            parentCluster?.AddExisting(actionNode);

            string moduleName = step.ModuleName ?? "Unknown";
            string accentColor = DesktopFlowGraphColours.GetColourForModule(moduleName);

            // Build inner HTML (Flow GraphBuilder pattern)
            string innerHtml = HtmlEncode($"#{step.Order}  {step.FullActionName}");

            // Parameters (up to 3, point-size 10)
            if (step.Parameters.Count > 0)
            {
                int count = 0;
                foreach (var param in step.Parameters)
                {
                    if (count >= 3)
                    {
                        innerHtml += "<br/><font point-size=\"10\">...</font>";
                        break;
                    }
                    string val = param.Value ?? "";
                    if (val.Length > 50) val = val.Substring(0, 50) + "...";
                    innerHtml += "<br/><font point-size=\"10\">" + generateMultiLineText(HtmlEncode(param.Key) + ": " + HtmlEncode(val)) + "</font>";
                    count++;
                }
            }

            // Output variables
            if (step.OutputVariables.Count > 0)
            {
                innerHtml += "<br/><font point-size=\"10\" color=\"#770bd6\">Output: " + HtmlEncode(string.Join(", ", step.OutputVariables)) + "</font>";
            }

            actionNode.SetAttributeHtml("label", generateCardHtml(accentColor, innerHtml));

            // Edge
            rootGraph.GetOrAddEdge(prevNode, actionNode, "e_" + prevNode.GetName() + "_" + nodeId);

            return actionNode;
        }

        private Node addControlFlowCluster(RootGraph rootGraph, FlowTreeNode treeNode, Node prevNode, SubGraph parentCluster, bool showSubactions)
        {
            var block = treeNode.ControlBlock;
            clusterCounter++;
            string clusterId = "cluster_" + clusterCounter;

            Graph parentGraph = (Graph)parentCluster ?? (Graph)rootGraph;
            SubGraph cluster = parentGraph.GetOrAddSubgraph(clusterId);
            cluster.SetAttribute("style", "filled,rounded");
            cluster.SetAttribute("fillcolor", DesktopFlowGraphColours.GetFillColourForBlock(block.Type));
            cluster.SetAttribute("color", DesktopFlowGraphColours.GetColourForBlock(block.Type));
            cluster.SetAttribute("penwidth", "2");

            // Label node inside the cluster (header card)
            string labelNodeId = "cf_" + clusterCounter;
            Node labelNode = rootGraph.GetOrAddNode(labelNodeId);
            labelNode.SetAttribute("shape", "plaintext");
            labelNode.SetAttribute("margin", "0");
            cluster.AddExisting(labelNode);

            string condition = block.Condition ?? "";
            if (condition.Length > 50) condition = condition.Substring(0, 50) + "...";
            string labelText = block.Type + (string.IsNullOrEmpty(condition) ? "" : ": " + condition);
            labelNode.SetAttributeHtml("label", generateCardHtml(
                DesktopFlowGraphColours.GetColourForBlock(block.Type), HtmlEncode(labelText)));

            // Edge from previous node to cluster label
            rootGraph.GetOrAddEdge(prevNode, labelNode, "e_to_" + labelNodeId);

            // Process children
            Node lastNode = labelNode;
            if (showSubactions)
            {
                foreach (var child in treeNode.Children)
                {
                    lastNode = addNodeToGraph(rootGraph, child, lastNode, cluster, showSubactions);
                }

                // Handle ELSE branch
                if (treeNode.ElseChildren.Count > 0)
                {
                    clusterCounter++;
                    string elseClusterId = "cluster_" + clusterCounter;
                    SubGraph elseCluster = cluster.GetOrAddSubgraph(elseClusterId);
                    elseCluster.SetAttribute("style", "filled,rounded");
                    elseCluster.SetAttribute("fillcolor", DesktopFlowGraphColours.ElseFillColour);
                    elseCluster.SetAttribute("color", DesktopFlowGraphColours.ElseColour);
                    elseCluster.SetAttribute("penwidth", "2");

                    string elseLabelId = "else_" + clusterCounter;
                    Node elseLabel = rootGraph.GetOrAddNode(elseLabelId);
                    elseLabel.SetAttribute("shape", "plaintext");
                    elseLabel.SetAttribute("margin", "0");
                    elseCluster.AddExisting(elseLabel);
                    elseLabel.SetAttributeHtml("label", generateCardHtml(
                        DesktopFlowGraphColours.ElseColour, HtmlEncode("ELSE")));

                    // Edge from IF label to ELSE (represents the "No" branch)
                    Edge elseEdge = rootGraph.GetOrAddEdge(labelNode, elseLabel, "e_else_" + clusterCounter);
                    elseEdge.SetAttribute("label", "No");

                    Node lastElseNode = elseLabel;
                    foreach (var child in treeNode.ElseChildren)
                    {
                        lastElseNode = addNodeToGraph(rootGraph, child, lastElseNode, elseCluster, showSubactions);
                    }
                }
            }

            // Exit anchor (invisible node at bottom of cluster for edge routing)
            string exitId = "exit_" + clusterId;
            Node exitNode = rootGraph.GetOrAddNode(exitId);
            exitNode.SetAttribute("shape", "point");
            exitNode.SetAttribute("width", "0");
            exitNode.SetAttribute("height", "0");
            exitNode.SetAttribute("style", "invis");
            cluster.AddExisting(exitNode);

            Edge exitEdge = rootGraph.GetOrAddEdge(lastNode, exitNode, "e_exit_" + clusterId);
            exitEdge.SetAttribute("style", "invis");

            return exitNode;
        }

        private Node addCollapsedControlNode(RootGraph rootGraph, FlowTreeNode treeNode, Node prevNode)
        {
            var block = treeNode.ControlBlock;
            clusterCounter++;
            string nodeId = "collapsed_" + clusterCounter;
            Node node = rootGraph.GetOrAddNode(nodeId);
            node.SetAttribute("shape", "plaintext");
            node.SetAttribute("margin", "0");

            string label = block.Type;
            if (!string.IsNullOrEmpty(block.Condition))
            {
                string cond = block.Condition.Length > 35 ? block.Condition.Substring(0, 35) + "..." : block.Condition;
                label += ": " + cond;
            }
            int childCount = countDescendants(treeNode);
            label += $" ({childCount} actions)";

            node.SetAttributeHtml("label", generateCardHtml(
                DesktopFlowGraphColours.ControlFlowColour, HtmlEncode(label)));

            rootGraph.GetOrAddEdge(prevNode, node, "e_collapsed_" + clusterCounter);
            return node;
        }

        private int countDescendants(FlowTreeNode node)
        {
            int count = 0;
            foreach (var child in node.Children)
            {
                if (child.Kind == TreeNodeKind.Action) count++;
                else count += countDescendants(child);
            }
            foreach (var child in node.ElseChildren)
            {
                if (child.Kind == TreeNodeKind.Action) count++;
                else count += countDescendants(child);
            }
            return count;
        }

        private string generateCardHtml(string accentColor, string innerHtml)
        {
            return "<table border=\"2\" cellborder=\"0\" cellspacing=\"0\" cellpadding=\"6\" color=\"" + accentColor + "\" bgcolor=\"white\" style=\"rounded\">"
                 + "<tr>"
                 + "<td>" + innerHtml + "</td>"
                 + "</tr></table>";
        }

        private string generateMultiLineText(string text)
        {
            string[] words = text.Split(' ');
            string multiLineText = "";
            int lineLength = 0;
            for (var counter = 0; counter < words.Length; counter++)
            {
                lineLength += words[counter].Length + 1;
                if (lineLength >= 65)
                {
                    multiLineText += "<br/>";
                    lineLength = 0;
                }
                multiLineText = multiLineText + words[counter] + " ";
            }
            return multiLineText;
        }

        private string generateImageFiles(RootGraph rootGraph, bool showSubactions)
        {
            string filename = filePrefix + (showSubactions ? "-detailed" : "");
            rootGraph.ToPngFile(folderPath + filename + ".png");
            rootGraph.ToSvgFile(folderPath + filename + ".svg");
            rootGraph.ToDotFile(folderPath + filename + ".dot");
            EmbedImagesInSvg(folderPath + filename + ".svg");
            return filename;
        }

        private static void EmbedImagesInSvg(string svgFilePath)
        {
            if (!File.Exists(svgFilePath)) return;
            string svgContent = File.ReadAllText(svgFilePath);
            string pattern = @"(xlink:href|href)=""([^""]+\.png)""";
            string result = Regex.Replace(svgContent, pattern, match =>
            {
                string attr = match.Groups[1].Value;
                string imagePath = match.Groups[2].Value;
                if (!Path.IsPathRooted(imagePath))
                    imagePath = Path.Combine(Path.GetDirectoryName(svgFilePath), imagePath);
                if (File.Exists(imagePath))
                {
                    byte[] imageBytes = File.ReadAllBytes(imagePath);
                    string base64 = Convert.ToBase64String(imageBytes);
                    return $"{attr}=\"data:image/png;base64,{base64}\"";
                }
                return match.Value;
            });
            File.WriteAllText(svgFilePath, result);
        }

        private static string HtmlEncode(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\r", " ");
        }
    }

    public static class DesktopFlowGraphColours
    {
        public static string TriggerColour = "#0077ff";
        public static string ExcelColour = "#217346";
        public static string WebAutomationColour = "#0078d4";
        public static string VariablesColour = "#770bd6";
        public static string MouseAndKeyboardColour = "#0078d4";
        public static string FolderColour = "#ca5010";
        public static string FileColour = "#ca5010";
        public static string TextColour = "#008080";
        public static string DateTimeColour = "#008080";
        public static string SystemColour = "#6b6b6b";
        public static string CloudConnectorColour = "#0078d4";
        public static string ControlFlowColour = "#484f58";
        public static string DefaultColour = "#484f58";

        public static string LoopFillColour = "#eef2f5";
        public static string IfFillColour = "#edf9ee";
        public static string ElseFillColour = "#feedec";
        public static string ElseColour = "#c04030";
        public static string OnErrorFillColour = "#fff4e5";

        public static string GetColourForModule(string moduleName)
        {
            return moduleName switch
            {
                "Excel" => ExcelColour,
                "WebAutomation" => WebAutomationColour,
                "Variables" => VariablesColour,
                "MouseAndKeyboard" => MouseAndKeyboardColour,
                "Folder" or "File" => FolderColour,
                "Text" => TextColour,
                "DateTime" => DateTimeColour,
                "System" => SystemColour,
                "CloudConnector" or "Connector" or "External" => CloudConnectorColour,
                "ControlFlow" => ControlFlowColour,
                "Trigger" => TriggerColour,
                "Flow" => "#0077ff",
                _ => DefaultColour,
            };
        }

        public static string GetColourForBlock(string blockType)
        {
            return blockType switch
            {
                "LOOP" => "#486991",
                "IF" => "#217346",
                "ELSE" => "#c04030",
                "ON ERROR" => "#ca5010",
                _ => ControlFlowColour,
            };
        }

        public static string GetFillColourForBlock(string blockType)
        {
            return blockType switch
            {
                "LOOP" => LoopFillColour,
                "IF" => IfFillColour,
                "ELSE" => ElseFillColour,
                "ON ERROR" => OnErrorFillColour,
                _ => "#f0f0f0",
            };
        }
    }
}
