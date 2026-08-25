using System;
using System.Collections.Generic;
using System.IO;
using PowerDocu.Common;
using Rubjerg.Graphviz;

namespace PowerDocu.ClassicWorkflowDocumenter
{
    /// <summary>
    /// Generates Graphviz flow diagrams for Classic Workflows, showing the step hierarchy
    /// with condition branches visualized as Yes/No clusters (matching the Flow documenter style).
    /// </summary>
    public class GraphBuilder
    {
        private readonly ClassicWorkflowEntity workflow;
        private readonly string folderPath;
        private HashSet<string> edges;

        public GraphBuilder(ClassicWorkflowEntity workflow, string path)
        {
            this.workflow = workflow;
            folderPath = path;
            Directory.CreateDirectory(folderPath);
        }

        public void BuildGraph()
        {
            if (workflow.Steps.Count == 0) return;

            edges = new HashSet<string>();
            RootGraph rootGraph = RootGraph.CreateNew(GraphType.Directed, CharsetHelper.GetSafeName(workflow.GetDisplayName()));
            Graph.IntroduceAttribute(rootGraph, "compound", "true");
            Graph.IntroduceAttribute(rootGraph, "fontname", "helvetica");
            Graph.IntroduceAttribute(rootGraph, "rankdir", "TB");
            Node.IntroduceAttribute(rootGraph, "shape", "");
            Node.IntroduceAttribute(rootGraph, "color", "");
            Node.IntroduceAttribute(rootGraph, "style", "");
            Node.IntroduceAttribute(rootGraph, "fillcolor", "");
            Node.IntroduceAttribute(rootGraph, "label", "");
            Node.IntroduceAttribute(rootGraph, "fontname", "helvetica");
            Edge.IntroduceAttribute(rootGraph, "label", "");
            Edge.IntroduceAttribute(rootGraph, "fontname", "helvetica");
            Edge.IntroduceAttribute(rootGraph, "fontsize", "10");

            // Add trigger node
            string triggerLabel = !string.IsNullOrEmpty(workflow.PrimaryEntity)
                ? "Trigger: " + workflow.PrimaryEntity
                : "Trigger";
            string triggerDetail = workflow.GetTriggerDescription();

            Node triggerNode = rootGraph.GetOrAddNode("trigger");
            triggerNode.SetAttribute("shape", "plaintext");
            triggerNode.SetAttribute("margin", "0");
            string triggerHtml = triggerLabel;
            if (!string.IsNullOrEmpty(triggerDetail) && triggerDetail != "None")
                triggerHtml += "<br/><font point-size=\"10\">" + System.Web.HttpUtility.HtmlEncode(triggerDetail) + "</font>";
            triggerNode.SetAttributeHtml("label", GenerateCardHtml(TriggerColor, triggerHtml));

            // Build step nodes and edges
            Node previousNode = triggerNode;
            int nodeCounter = 0;
            AddStepNodes(rootGraph, workflow.Steps, ref previousNode, null, ref nodeCounter);

            try
            {
                rootGraph.CreateLayout();
                string filename = "workflow";
                rootGraph.ToPngFile(folderPath + filename + ".png");
                rootGraph.ToSvgFile(folderPath + filename + ".svg");
                rootGraph.ToDotFile(folderPath + filename + ".dot");

                // Embed images as base64 in SVG for Word output compatibility
                EmbedSvgImages(folderPath + filename + ".svg");

                NotificationHelper.SendNotification("  - Created workflow graph " + folderPath + filename + ".png");
            }
            catch (Exception ex)
            {
                NotificationHelper.SendNotification("  - Warning: Could not create workflow graph: " + ex.Message);
            }
        }

        private void AddStepNodes(RootGraph graph, List<ClassicWorkflowStep> steps, ref Node previousNode, SubGraph parentCluster, ref int nodeCounter)
        {
            foreach (var step in steps)
            {
                nodeCounter++;
                string nodeId = "step_" + nodeCounter;
                string accentColor = GetColorForStepType(step.StepType);
                string nodeLabel = GetNodeLabel(step);

                if ((step.StepType == ClassicWorkflowStepType.CheckCondition ||
                     step.StepType == ClassicWorkflowStepType.Wait) && step.ChildSteps.Count > 0)
                {
                    // Create a cluster for the condition and its branches
                    SubGraph condCluster = parentCluster != null
                        ? parentCluster.GetOrAddSubgraph("cluster_" + nodeId)
                        : graph.GetOrAddSubgraph("cluster_" + nodeId);
                    condCluster.SafeSetAttribute("style", "filled", "");
                    condCluster.SafeSetAttribute("fillcolor", ClusterFillColor, "");
                    condCluster.SafeSetAttribute("color", ClusterBorderColor, "");
                    condCluster.SafeSetAttribute("label", "", "");

                    // Add the condition node inside the cluster
                    Node condNode = graph.GetOrAddNode(nodeId);
                    condNode.SetAttribute("shape", "plaintext");
                    condNode.SetAttribute("margin", "0");
                    condNode.SetAttributeHtml("label", GenerateCardHtml(accentColor, nodeLabel));
                    condCluster.AddExisting(condNode);

                    // Connect from previous node
                    ConnectNodes(graph, previousNode, condNode);

                    // Process child branches
                    Node lastBranchNode = condNode;
                    foreach (var childStep in step.ChildSteps)
                    {
                        if (childStep.StepType == ClassicWorkflowStepType.ConditionBranch && childStep.ChildSteps.Count > 0)
                        {
                            nodeCounter++;
                            string branchClusterId = "cluster_branch_" + nodeCounter;
                            bool isThenBranch = childStep == step.ChildSteps[0]; // first branch is "Then"
                            SubGraph branchCluster = condCluster.GetOrAddSubgraph(branchClusterId);
                            branchCluster.SafeSetAttribute("style", "filled", "");
                            branchCluster.SafeSetAttribute("fillcolor", isThenBranch ? YesFillColor : NoFillColor, "");
                            branchCluster.SafeSetAttribute("color", isThenBranch ? YesBorderColor : NoBorderColor, "");
                            branchCluster.SafeSetAttribute("label", isThenBranch ? "Yes" : "Otherwise", "");
                            branchCluster.SafeSetAttribute("fontname", "helvetica", "");

                            // Add nodes within the branch
                            Node branchPrev = condNode;
                            AddStepNodes(graph, childStep.ChildSteps, ref branchPrev, branchCluster, ref nodeCounter);
                        }
                        else if (childStep.StepType == ClassicWorkflowStepType.ConditionBranch)
                        {
                            // Empty branch — skip
                        }
                        else
                        {
                            // Non-branch child step inside condition
                            nodeCounter++;
                            string childNodeId = "step_" + nodeCounter;
                            Node childNode = graph.GetOrAddNode(childNodeId);
                            childNode.SetAttribute("shape", "plaintext");
                            childNode.SetAttribute("margin", "0");
                            string childColor = GetColorForStepType(childStep.StepType);
                            childNode.SetAttributeHtml("label", GenerateCardHtml(childColor, GetNodeLabel(childStep)));
                            condCluster.AddExisting(childNode);
                            ConnectNodes(graph, lastBranchNode, childNode);
                            lastBranchNode = childNode;

                            // Recurse if this step also has children
                            if (childStep.ChildSteps.Count > 0)
                            {
                                AddStepNodes(graph, childStep.ChildSteps, ref lastBranchNode, condCluster, ref nodeCounter);
                            }
                        }
                    }

                    previousNode = condNode; // next step connects from the condition node
                }
                else
                {
                    // Regular step (non-condition)
                    Node stepNode = graph.GetOrAddNode(nodeId);
                    stepNode.SetAttribute("shape", "plaintext");
                    stepNode.SetAttribute("margin", "0");
                    stepNode.SetAttributeHtml("label", GenerateCardHtml(accentColor, nodeLabel));

                    if (parentCluster != null)
                        parentCluster.AddExisting(stepNode);

                    ConnectNodes(graph, previousNode, stepNode);
                    previousNode = stepNode;

                    // Recurse into child steps (for composites, etc.)
                    if (step.ChildSteps.Count > 0)
                    {
                        AddStepNodes(graph, step.ChildSteps, ref previousNode, parentCluster, ref nodeCounter);
                    }
                }
            }
        }

        private void ConnectNodes(RootGraph graph, Node from, Node to)
        {
            string edgeName = from.GetName() + "->" + to.GetName();
            if (edges.Add(edgeName))
            {
                graph.GetOrAddEdge(from, to, edgeName);
            }
        }

        private string GetNodeLabel(ClassicWorkflowStep step)
        {
            string typeLabel = step.GetStepTypeLabel();
            string name = step.Name ?? "";
            string entityInfo = "";

            if (!string.IsNullOrEmpty(step.TargetEntity))
                entityInfo = "<br/><font point-size=\"10\">" + System.Web.HttpUtility.HtmlEncode(step.TargetEntity) + "</font>";

            // For certain types, show the type as a prefix
            if (step.StepType == ClassicWorkflowStepType.CheckCondition ||
                step.StepType == ClassicWorkflowStepType.ConditionBranch)
            {
                return System.Web.HttpUtility.HtmlEncode(name) + entityInfo;
            }

            if (name == typeLabel || string.IsNullOrEmpty(name))
                return System.Web.HttpUtility.HtmlEncode(typeLabel) + entityInfo;

            return "<font point-size=\"10\">" + System.Web.HttpUtility.HtmlEncode(typeLabel) + "</font><br/>"
                 + System.Web.HttpUtility.HtmlEncode(Truncate(name, 60)) + entityInfo;
        }

        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLen) return text;
            return text.Substring(0, maxLen - 3) + "...";
        }

        private static string GenerateCardHtml(string accentColor, string innerHtml)
        {
            return "<table border=\"2\" cellborder=\"0\" cellspacing=\"0\" cellpadding=\"6\" color=\"" + accentColor + "\" bgcolor=\"white\" style=\"rounded\">"
                 + "<tr><td>" + innerHtml + "</td></tr></table>";
        }

        private static void EmbedSvgImages(string svgPath)
        {
            try
            {
                var xmlDoc = new System.Xml.XmlDocument { XmlResolver = null };
                xmlDoc.Load(svgPath);
                var elemList = xmlDoc.GetElementsByTagName("image");
                foreach (System.Xml.XmlNode xn in elemList)
                {
                    var href = xn.Attributes?["xlink:href"];
                    if (href != null && !href.Value.StartsWith("data:"))
                        href.Value = "data:image/png;base64," + ImageHelper.GetBase64(href.Value);
                }
                xmlDoc.Save(svgPath);
            }
            catch { }
        }

        // ── Color palette (matching Flow documenter style) ──
        private const string TriggerColor = "#0077ff";
        private const string ConditionColor = "#484f58";
        private const string UpdateColor = "#088142";
        private const string CreateColor = "#088142";
        private const string EmailColor = "#0078d4";
        private const string AssignColor = "#770bd6";
        private const string StatusColor = "#8c3900";
        private const string StopColor = "#f41700";
        private const string WaitColor = "#8c6cff";
        private const string CustomColor = "#486991";
        private const string ClientColor = "#486991";
        private const string DefaultColor = "#0077ff";

        private const string ClusterFillColor = "#f5f5f5";
        private const string ClusterBorderColor = "#484f58";
        private const string YesFillColor = "#edf9ee";
        private const string YesBorderColor = "#88da8d";
        private const string NoFillColor = "#feedec";
        private const string NoBorderColor = "#fb8981";

        private static string GetColorForStepType(ClassicWorkflowStepType stepType)
        {
            return stepType switch
            {
                ClassicWorkflowStepType.CheckCondition => ConditionColor,
                ClassicWorkflowStepType.ConditionBranch => ConditionColor,
                ClassicWorkflowStepType.UpdateRecord => UpdateColor,
                ClassicWorkflowStepType.CreateRecord => CreateColor,
                ClassicWorkflowStepType.SendEmail => EmailColor,
                ClassicWorkflowStepType.Assign => AssignColor,
                ClassicWorkflowStepType.ChangeStatus => StatusColor,
                ClassicWorkflowStepType.Stop => StopColor,
                ClassicWorkflowStepType.Wait => WaitColor,
                ClassicWorkflowStepType.Custom => CustomColor,
                ClassicWorkflowStepType.SetVisibility or
                ClassicWorkflowStepType.SetDisplayMode or
                ClassicWorkflowStepType.SetFieldRequired or
                ClassicWorkflowStepType.SetAttributeValue or
                ClassicWorkflowStepType.SetDefaultValue or
                ClassicWorkflowStepType.SetMessage => ClientColor,
                _ => DefaultColor
            };
        }
    }
}
