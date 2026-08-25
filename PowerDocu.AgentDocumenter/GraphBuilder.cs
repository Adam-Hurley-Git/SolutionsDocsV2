using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using PowerDocu.Common;
using Rubjerg.Graphviz;
using Svg;
using YamlDotNet.RepresentationModel;

namespace PowerDocu.AgentDocumenter
{
    public class GraphBuilder
    {
        private readonly BotComponent topic;
        private readonly string folderPath;
        private readonly DocumentationContext context;
        private YamlScalarNode actionsKey = new YamlScalarNode("actions");
        private YamlScalarNode conditions = new YamlScalarNode("conditions");
        private YamlScalarNode elseActions = new YamlScalarNode("elseActions");

        public GraphBuilder(string agentName, BotComponent topicToUse, string path, DocumentationContext context = null)
        {
            topic = topicToUse;
            folderPath = path;
            this.context = context;
            Directory.CreateDirectory(folderPath + "Resources");
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
            RootGraph rootGraph = RootGraph.CreateNew(GraphType.Directed, CharsetHelper.GetSafeName(topic.Name + "(" + topic.getTopicFileName() + ")"));
            Graph.IntroduceAttribute(rootGraph, "rankdir", "TB");
            Graph.IntroduceAttribute(rootGraph, "compound", "true");
            Graph.IntroduceAttribute(rootGraph, "fontname", "helvetica");

            // Add subgraph default attributes
            Graph.IntroduceAttribute(rootGraph, "clusterrank", "local");
            SubGraph.IntroduceAttribute(rootGraph, "style", "filled");
            SubGraph.IntroduceAttribute(rootGraph, "color", "black");
            SubGraph.IntroduceAttribute(rootGraph, "fillcolor", "lightgray");
            SubGraph.IntroduceAttribute(rootGraph, "penwidth", "1");

            Node.IntroduceAttribute(rootGraph, "shape", "rectangle");
            Node.IntroduceAttribute(rootGraph, "color", "");
            Node.IntroduceAttribute(rootGraph, "style", "");
            Node.IntroduceAttribute(rootGraph, "fillcolor", "");
            Node.IntroduceAttribute(rootGraph, "label", "");
            Node.IntroduceAttribute(rootGraph, "fontname", "helvetica");
            Edge.IntroduceAttribute(rootGraph, "label", "");
            var mapping = topic.GetYamlMappingNode();

            // Guard: skip graph generation for non-AdaptiveDialog topics (e.g. KnowledgeSourceConfiguration)
            if (!mapping.Children.TryGetValue(new YamlScalarNode("beginDialog"), out var beginDialogNode)
                || !(beginDialogNode is YamlMappingNode triggerYamlNode))
            {
                // Create a simple single-node graph for non-dialog topics
                string topicKind = "Unknown";
                if (mapping.Children.TryGetValue(new YamlScalarNode("kind"), out var kindNode))
                    topicKind = kindNode.ToString();
                Node infoNode = rootGraph.GetOrAddNode("info_" + topic.Name);
                var svgDoc = SvgDocument.FromSvg<SvgDocument>(AgentIcon.GetIcon("KnowledgeSource"));
                using (var bmp = svgDoc.Draw(20, 0))
                {
                    bmp?.Save(folderPath + @"Resources\KnowledgeSource.png");
                }
                infoNode.SetAttribute("shape", "plain");
                string infoHtml = createCardStart("Trigger", topicKind + ": " + topic.Name, "KnowledgeSource");
                infoHtml += createCardEnd();
                infoNode.SetAttributeHtml("label", infoHtml);
                rootGraph.CreateLayout();
                NotificationHelper.SendNotification("  - Created Graph " + folderPath + generateImageFiles(rootGraph, showSubactions) + ".png");
                return;
            }
            var triggerYaml = triggerYamlNode;
            Node trigger = addTriggerDetails(triggerYaml, rootGraph);
            addActionNodes(triggerYaml, trigger, rootGraph, actionsKey);
            rootGraph.CreateLayout();
            NotificationHelper.SendNotification("  - Created Graph " + folderPath + generateImageFiles(rootGraph, showSubactions) + ".png");
        }

        private Node addTriggerDetails(YamlMappingNode triggerYaml, RootGraph rootGraph)
        {
            string triggerType = triggerYaml.Children[new YamlScalarNode("kind")].ToString();
            Node trigger = rootGraph.GetOrAddNode("Trigger " + topic.Name + " (" + topic.getTopicFileName() + ")");
            trigger.SetAttribute("shape", "plain");
            var svgDocument = SvgDocument.FromSvg<SvgDocument>(AgentIcon.GetIcon("Trigger"));
            //generating the PNG from the SVG with a width of 20px because some SVGs are huge and downscaled, thus can't be shown directly
            using (var bitmap = svgDocument.Draw(20, 0))
            {
                bitmap?.Save(folderPath + @"Resources\Trigger.png");
            }
            string html = createCardStart("Trigger", $"Trigger - {topic.Name}");
            switch (triggerType)
            {
                case "OnRecognizedIntent":

                    html += createCardBodyRow("<table border=\"0\" cellpadding=\"4\"><tr><td>The agent chooses</td></tr></table>");
                    html += createCardBodyRow("Describe what the topic does:");
                    html += "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\"><tr><td>This tool can handle queries like these:<br/>";
                    var intentYaml = (YamlMappingNode)triggerYaml.Children[new YamlScalarNode("intent")];
                    if (intentYaml.Children.TryGetValue(new YamlScalarNode("triggerQueries"), out var triggerQueryNode) && triggerQueryNode is YamlSequenceNode triggerQuerySequence)
                    {
                        html += string.Join("<br/>", triggerQuerySequence);
                    }
                    html += "</td></tr></table></td></tr>";

                    break;
                case "OnConversationStart":
                    html += createCardBodyRow("<table border=\"0\" cellpadding=\"4\"><tr><td>When a conversation starts</td></tr></table>");
                    break;
                case "OnEscalate":
                    html += createCardBodyRow("<table border=\"0\" cellpadding=\"4\"><tr><td>Talk to a representative</td></tr></table>");
                    if (triggerYaml.Children.TryGetValue(new YamlScalarNode("intent"), out var escIntentNode)
                        && escIntentNode is YamlMappingNode escIntentMapping
                        && escIntentMapping.Children.TryGetValue(new YamlScalarNode("triggerQueries"), out var escTriggerNode)
                        && escTriggerNode is YamlSequenceNode escTriggerSeq)
                    {
                        html += "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\"><tr><td>";
                        html += string.Join("<br/>", escTriggerSeq.Take(10));
                        if (escTriggerSeq.Count() > 10)
                            html += "<br/>... and more";
                        html += "</td></tr></table></td></tr>";
                    }
                    break;
                case "OnUnknownIntent":
                    html += createCardBodyRow("<table border=\"0\" cellpadding=\"4\"><tr><td>Unknown / unmatched intent (Fallback)</td></tr></table>");
                    break;
                case "OnRedirect":
                    html += createCardBodyRow("<table border=\"0\" cellpadding=\"4\"><tr><td>Redirected from another topic</td></tr></table>");
                    break;
                case "OnSystemRedirect":
                    html += createCardBodyRow("<table border=\"0\" cellpadding=\"4\"><tr><td>System redirect</td></tr></table>");
                    break;
                case "OnError":
                    html += createCardBodyRow("<table border=\"0\" cellpadding=\"4\"><tr><td>When an error occurs</td></tr></table>");
                    break;
                case "OnSignIn":
                    html += createCardBodyRow("<table border=\"0\" cellpadding=\"4\"><tr><td>When sign-in is required</td></tr></table>");
                    break;
                case "OnSelectIntent":
                    html += createCardBodyRow("<table border=\"0\" cellpadding=\"4\"><tr><td>When multiple topics matched</td></tr></table>");
                    break;
                case "OnActivity":
                    html += createCardBodyRow("<table border=\"0\" cellpadding=\"4\"><tr><td>When an activity is received</td></tr></table>");
                    if (triggerYaml.Children.TryGetValue(new YamlScalarNode("type"), out var activityTypeNode))
                    {
                        html += createCardBodyRow($"<b>Activity Type:</b> {System.Web.HttpUtility.HtmlEncode(activityTypeNode.ToString())}");
                    }
                    if (triggerYaml.Children.TryGetValue(new YamlScalarNode("condition"), out var activityConditionNode))
                    {
                        html += createCardBodyRow("<b>Condition:</b>");
                        html += $"<tr><td cellpadding=\"8\" align=\"left\" bgcolor=\"white\">{formatConditionExpression(activityConditionNode.ToString())}</td></tr>";
                    }
                    break;
                default:
                    html += createCardBodyRow($"<table border=\"0\" cellpadding=\"4\"><tr><td>{System.Web.HttpUtility.HtmlEncode(triggerType)}</td></tr></table>");
                    break;
            }
            html += createCardEnd();
            trigger.SetAttributeHtml("label", html);
            return trigger;
        }

        private Node addActionNodes(YamlMappingNode actionsYaml, Node prevNode, RootGraph rootGraph, YamlScalarNode filterKey, SubGraph parentCluster = null)
        {
            Node returnNode = null;
            if (actionsYaml.Children.TryGetValue(filterKey, out var actionsNode) && actionsNode is YamlSequenceNode actionsSequence)
            {
                foreach (var action in actionsSequence)
                {
                    Node clusterExitNode = null;
                    Node actionNode = null;
                    SubGraph conditionCluster = null;
                    string actionName = CharsetHelper.GetSafeName(((YamlMappingNode)action).Children[new YamlScalarNode("kind")].ToString() + ((YamlMappingNode)action).Children[new YamlScalarNode("id")].ToString());
                    string actionType = GetActionType((YamlMappingNode)action);
                    if (actionType != "ConditionGroup")
                    {
                        actionNode = rootGraph.GetOrAddNode(actionName);
                        actionNode.SetAttribute("shape", "plain");
                        parentCluster?.AddExisting(actionNode);
                        returnNode = actionNode;
                    }
                    string displayName = "";
                    if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("displayName"), out var displayNameNode))
                    {
                        displayName = System.Web.HttpUtility.HtmlEncode(displayNameNode.ToString());
                    }
                    var svgDocument = SvgDocument.FromSvg<SvgDocument>(AgentIcon.GetIcon(actionType));
                    //generating the PNG from the SVG with a width of 16px because some SVGs are huge and downscaled, thus can't be shown directly
                    using (var bitmap = svgDocument.Draw(20, 0))
                    {
                        bitmap?.Save(folderPath + @"Resources\" + actionType + ".png");
                    }
                    switch (actionType)
                    {
                        case "AdaptiveCard":

                            string adapativeCardHtml = createCardStart(actionType, $"Adaptive Card: {displayName}");

                            // Parse the card JSON content
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("card"), out var cardNode))
                            {
                                adapativeCardHtml += renderAdaptiveCardContent(cardNode.ToString());
                            }
                            else
                            {
                                adapativeCardHtml += createCardBodyRow("Adaptive Card definition not found");
                            }

                            // Process output binding if present
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("output"), out var outputNode) &&
                                outputNode is YamlMappingNode outputMapping &&
                                outputMapping.Children.TryGetValue(new YamlScalarNode("binding"), out var bindingNode) &&
                                bindingNode is YamlMappingNode outputBinding)
                            {
                                adapativeCardHtml += "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\">";
                                adapativeCardHtml += "<tr><td><b>Output Binding:</b></td></tr>";
                                foreach (var outputItem in outputBinding.Children)
                                {
                                    adapativeCardHtml += $"<tr><td><b>{outputItem.Key}:</b> {formatVariable(outputItem.Value.ToString())}</td></tr>";
                                }
                                adapativeCardHtml += "</table></td></tr>";
                            }

                            adapativeCardHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", adapativeCardHtml);
                            break;
                        case "Question":
                            string questionHtml = createCardStart(actionType, $"Question: {displayName}");

                            // Get the prompt text
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("prompt"), out var promptNode))
                            {
                                string promptText = promptNode.ToString();
                                questionHtml += "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\">";
                                questionHtml += $"<tr><td>{generateMultiLineText(System.Web.HttpUtility.HtmlEncode(promptText))}</td></tr>";
                                questionHtml += "</table></td></tr>";
                            }


                            // Get the entity type
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("entity"), out var entityNode))
                            {
                                questionHtml += createCardBodyRow(formatEntityNode(entityNode));
                            }

                            // Get the variable name
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("variable"), out var variableNode))
                            {
                                string variableName = variableNode.ToString();
                                // Strip init: prefix - it's internal metadata
                                if (variableName.StartsWith("init:", StringComparison.OrdinalIgnoreCase))
                                    variableName = variableName.Substring(5);
                                questionHtml += createCardBodyRow($"<b>Save user response as:</b><br/> {formatVariable(variableName)}");
                            }

                            // Check for choices if it's a choice question
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("choices"), out var choicesNode) && choicesNode is YamlSequenceNode choicesSequence)
                            {
                                questionHtml += createCardBodyRow("<b>Choices:</b>");
                                questionHtml += "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\">";
                                foreach (var choice in choicesSequence)
                                {
                                    if (choice is YamlMappingNode choiceMap)
                                    {
                                        string choiceText = "";
                                        if (choiceMap.Children.TryGetValue(new YamlScalarNode("value"), out var valueNode))
                                        {
                                            choiceText = valueNode.ToString();
                                        }
                                        if (choiceMap.Children.TryGetValue(new YamlScalarNode("synonyms"), out var synonymsNode) && synonymsNode is YamlSequenceNode synonymsSeq)
                                        {
                                            var synonymsList = synonymsSeq.Select(s => s.ToString()).ToList();
                                            choiceText += $" (synonyms: {string.Join(", ", synonymsList)})";
                                        }
                                        questionHtml += $"<tr><td>{System.Web.HttpUtility.HtmlEncode(choiceText)}</td></tr>";
                                    }
                                    else
                                    {
                                        // Simple string choice
                                        questionHtml += $"<tr><td>{System.Web.HttpUtility.HtmlEncode(choice.ToString())}</td></tr>";
                                    }
                                }
                                questionHtml += "</table></td></tr>";
                            }

                            questionHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", questionHtml);
                            break;
                        case "ConditionGroup":
                            // Create the condition cluster - use the parentCluster if we're nested
                            conditionCluster = ((Graph)(parentCluster != null ? parentCluster : rootGraph)).GetOrAddSubgraph("cluster_" + CharsetHelper.GetSafeName(actionName));

                            // Use SetAttribute instead of SafeSetAttribute for better reliability
                            conditionCluster.SetAttribute("style", "filled");
                            conditionCluster.SetAttribute("fillcolor", GraphColours.GetFillColourForAction(actionType));
                            conditionCluster.SetAttribute("color", GraphColours.GetColourForAction(actionType));
                            conditionCluster.SetAttribute("penwidth", "1");

                            // Track the last nodes in each condition branch
                            var lastNodes = new List<Node>();
                            // Collect all nodes that should be at the same level
                            var sameLevelNodes = new List<Node>();
                            //get the conditions node, then loop through the items inside which may have actions
                            if (((YamlMappingNode)action).Children.TryGetValue(conditions, out var conditionsNode) && conditionsNode is YamlSequenceNode conditionsSequence)
                            {
                                foreach (var condition in conditionsSequence)
                                {
                                    //add the condition node
                                    Node conditionNode = rootGraph.GetOrAddNode("conditionnode-" + CharsetHelper.GetSafeName(((YamlMappingNode)condition).Children[new YamlScalarNode("id")].ToString()));
                                    conditionNode.SetAttribute("shape", "plain");
                                    string rawCondition = ((YamlMappingNode)condition).Children[new YamlScalarNode("condition")].ToString();
                                    string conditionHtml = createCardStart(actionType, "Condition");
                                    conditionHtml += createCardBodyRow(formatConditionExpression(rawCondition));
                                    conditionHtml += createCardEnd();
                                    conditionNode.SetAttributeHtml("label", conditionHtml);

                                    // Create edge from previous node to condition node
                                    Edge edge = rootGraph.GetOrAddEdge(prevNode, conditionNode, "edge to " + "conditionnode-" + CharsetHelper.GetSafeName(((YamlMappingNode)condition).Children[new YamlScalarNode("id")].ToString()));
                                    edge.SetAttribute("weight", "1");
                                    conditionCluster.AddExisting(conditionNode);

                                    // Process actions and get the last node in this branch - PASS conditionCluster as parentCluster for nesting
                                    Node lastNodeInBranch = addActionNodes((YamlMappingNode)condition, conditionNode, rootGraph, actionsKey, conditionCluster);
                                    lastNodes.Add(lastNodeInBranch ?? conditionNode);
                                    sameLevelNodes.Add(conditionNode);
                                }
                            }

                            // Handle else actions if present
                            if (((YamlMappingNode)action).Children.TryGetValue(elseActions, out var elseActionsYamlNode))
                            {
                                //add the else actions node
                                Node elseActionsNode = rootGraph.GetOrAddNode("elseactionsnode-" + actionName);
                                elseActionsNode.SetAttribute("shape", "plain");
                                string elseHtml = createCardStart(actionType, "Condition");
                                elseHtml += createCardBodyRow("All Other Conditions");
                                elseHtml += createCardEnd();
                                elseActionsNode.SetAttributeHtml("label", elseHtml);
                                Edge edge = rootGraph.GetOrAddEdge(prevNode, elseActionsNode, "edge to " + "elseactionsnode-" + actionName);
                                edge.SetAttribute("weight", "1");
                                conditionCluster.AddExisting(elseActionsNode);

                                // Process else actions - PASS conditionCluster as parentCluster for nesting
                                Node lastElseNode = addActionNodes((YamlMappingNode)action, elseActionsNode, rootGraph, elseActions, conditionCluster);
                                lastNodes.Add(lastElseNode ?? elseActionsNode);
                                sameLevelNodes.Add(elseActionsNode);
                            }

                            // Create exit node AFTER processing all conditions
                            clusterExitNode = rootGraph.GetOrAddNode(actionName + "_cluster_exit");
                            clusterExitNode.SetAttribute("margin", "0");
                            clusterExitNode.SetAttribute("style", "invis");
                            clusterExitNode.SetAttribute("width", "0");
                            clusterExitNode.SetAttribute("height", "0");
                            clusterExitNode.SetAttribute("shape", "point");
                            conditionCluster.AddExisting(clusterExitNode);

                            // Connect all last nodes to the exit node with invisible edges
                            foreach (var lastNode in lastNodes)
                            {
                                if (lastNode != null)
                                {
                                    Edge exitEdge = rootGraph.GetOrAddEdge(lastNode, clusterExitNode, "to_exit_" + lastNode.GetName());
                                    exitEdge.SetAttribute("style", "invis");
                                    exitEdge.SetAttribute("weight", "100");
                                    exitEdge.SetAttribute("minlen", "1");
                                }
                            }

                            returnNode = clusterExitNode;
                            prevNode = clusterExitNode;
                            break;
                        case "AIModel":
                            string aiModelHtml = createCardStart(actionType, !string.IsNullOrEmpty(displayName) ? displayName : "Prompt");

                            // Show AI Model name (or ID as fallback)
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("aIModelId"), out var aiModelIdNode))
                            {
                                string aiModelIdValue = aiModelIdNode.ToString();
                                string aiModelName = context?.GetAIModelNameById(aiModelIdValue);
                                string aiModelLabel = !string.IsNullOrEmpty(aiModelName)
                                    ? $"<b>Prompt:</b> {System.Web.HttpUtility.HtmlEncode(aiModelName)}"
                                    : $"<b>AI Model ID:</b> {generateMultiLineText(System.Web.HttpUtility.HtmlEncode(aiModelIdValue))}";
                                aiModelHtml += createCardBodyRow($"{aiModelLabel}");
                            }

                            // Show input bindings
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("input"), out var aiInputNode)
                                && aiInputNode is YamlMappingNode aiInputMapping
                                && aiInputMapping.Children.TryGetValue(new YamlScalarNode("binding"), out var aiBindingNode)
                                && aiBindingNode is YamlMappingNode aiBindingMapping)
                            {
                                aiModelHtml += "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\"><tr><td><b>Inputs:</b></td></tr>";
                                foreach (var kvp in aiBindingMapping.Children)
                                {
                                    aiModelHtml += $"<tr><td><b>{System.Web.HttpUtility.HtmlEncode(kvp.Key.ToString())}:</b> {formatVariable(kvp.Value.ToString())}</td></tr>";
                                }
                                aiModelHtml += "</table></td></tr>";
                            }

                            // Show output bindings
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("output"), out var aiOutputNode)
                                && aiOutputNode is YamlMappingNode aiOutputMapping
                                && aiOutputMapping.Children.TryGetValue(new YamlScalarNode("binding"), out var aiOutputBindingNode)
                                && aiOutputBindingNode is YamlMappingNode aiOutputBindingMapping)
                            {
                                aiModelHtml += "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\"><tr><td><b>Outputs:</b></td></tr>";
                                foreach (var kvp in aiOutputBindingMapping.Children)
                                {
                                    aiModelHtml += $"<tr><td><b>{System.Web.HttpUtility.HtmlEncode(kvp.Key.ToString())}:</b> {formatVariable(kvp.Value.ToString())}</td></tr>";
                                }
                                aiModelHtml += "</table></td></tr>";
                            }

                            aiModelHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", aiModelHtml);
                            break;
                        case "Message":
                            YamlScalarNode messageYaml = null;
                            string messageContent = null;
                            var activityYaml = ((YamlMappingNode)action).Children[new YamlScalarNode("activity")];
                            if (activityYaml is YamlMappingNode activityMapping)
                            {
                                if (activityMapping.Children.TryGetValue(new YamlScalarNode("text"), out var textNode) && textNode is YamlSequenceNode textSequence)
                                {
                                    messageYaml = (YamlScalarNode)textSequence.First();
                                }
                                else if (activityMapping.Children.TryGetValue(new YamlScalarNode("attachments"), out var attachmentsNode) && attachmentsNode is YamlSequenceNode attachmentsSequence)
                                {
                                    // Activity contains attachments (e.g. AdaptiveCard) instead of text
                                    messageContent = "";
                                    foreach (var attachment in attachmentsSequence)
                                    {
                                        if (attachment is YamlMappingNode attachMap && attachMap.Children.TryGetValue(new YamlScalarNode("kind"), out var kindNode))
                                        {
                                            string attachKind = kindNode.ToString();
                                            if (attachKind == "AdaptiveCardTemplate" && attachMap.Children.TryGetValue(new YamlScalarNode("cardContent"), out var cardContentNode))
                                            {
                                                messageContent += renderAdaptiveCardContent(cardContentNode.ToString());
                                            }
                                            else
                                            {
                                                messageContent += createCardBodyRow($"[Attachment: {System.Web.HttpUtility.HtmlEncode(attachKind)}]");
                                            }
                                        }
                                    }
                                    if (string.IsNullOrEmpty(messageContent)) messageContent = null;
                                }
                            }
                            else if (activityYaml is YamlScalarNode scalarActivity)
                            {
                                messageYaml = scalarActivity;
                            }
                            string msgHtml = createCardStart(actionType, "Message");
                            if (messageYaml != null)
                                msgHtml += createCardBodyRow(CharsetHelper.GetSafeName(messageYaml.Value).Replace("\n", "<br/>"));
                            else if (messageContent != null)
                                msgHtml += messageContent;
                            else
                                msgHtml += createCardBodyRow("(message content)");
                            msgHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", msgHtml);
                            break;
                        case "SetVariable":
                            var variableYaml = (YamlScalarNode)((YamlMappingNode)action).Children[new YamlScalarNode("variable")];
                            var valueYaml = (YamlScalarNode)((YamlMappingNode)action).Children[new YamlScalarNode("value")];
                            string setVarHtml = createCardStart(actionType, "Set Variable");
                            setVarHtml += createCardBodyRow($"<table border=\"1\" cellpadding=\"4\"><tr><td>{formatVariable(variableYaml.Value)}</td></tr></table>");
                            setVarHtml += createCardBodyRow("To Value");
                            string variableValue = valueYaml.Value;
                            if (variableValue.StartsWith('='))
                            {
                                variableValue = variableValue.Substring(1);
                            }
                            setVarHtml += createCardBodyRow($"<table border=\"1\" cellpadding=\"4\"><tr><td>{generateMultiLineText(System.Web.HttpUtility.HtmlEncode(variableValue))}</td></tr></table>");
                            setVarHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", setVarHtml);
                            break;
                        case "CancelAllDialogs":
                            actionNode.SetAttributeHtml("label", createCardStart(actionType, !string.IsNullOrEmpty(displayName) ? displayName : "End all topics") + createCardEnd());
                            break;
                        case "LogCustomTelemetry":
                            actionNode.SetAttributeHtml("label", createCardStart(actionType, !string.IsNullOrEmpty(displayName) ? displayName : "Log custom telemetry event") + createCardEnd());
                            break;
                        case "InvokeFlow":
                            string flowHtml = createCardStart(actionType, !string.IsNullOrEmpty(displayName) ? $"Flow: {displayName}" : "Call a flow");
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("flowId"), out var flowIdNode))
                            {
                                string flowIdValue = flowIdNode.ToString();
                                string flowName = context?.GetFlowNameById(flowIdValue);
                                string flowLabel = !string.IsNullOrEmpty(flowName)
                                    ? $"<b>Flow: </b> {System.Web.HttpUtility.HtmlEncode(flowName)}"
                                    : $"<b>Flow ID: </b> {generateMultiLineText(System.Web.HttpUtility.HtmlEncode(flowIdValue))}";
                                flowHtml += createCardBodyRow($"{flowLabel}");
                            }
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("input"), out var flowInputNode)
                                && flowInputNode is YamlMappingNode flowInputMapping
                                && flowInputMapping.Children.TryGetValue(new YamlScalarNode("binding"), out var flowBindingNode)
                                && flowBindingNode is YamlMappingNode flowBindingMapping)
                            {
                                flowHtml += "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\"><tr><td><b>Inputs:</b></td></tr>";
                                foreach (var kvp in flowBindingMapping.Children)
                                {
                                    flowHtml += $"<tr><td><b>{System.Web.HttpUtility.HtmlEncode(kvp.Key.ToString())}:</b> {formatVariable(kvp.Value.ToString())}</td></tr>";
                                }
                                flowHtml += "</table></td></tr>";
                            }
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("output"), out var flowOutputNode)
                                && flowOutputNode is YamlMappingNode flowOutputMapping
                                && flowOutputMapping.Children.TryGetValue(new YamlScalarNode("binding"), out var flowOutputBindingNode)
                                && flowOutputBindingNode is YamlMappingNode flowOutputBindingMapping)
                            {
                                flowHtml += "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\"><tr><td><b>Outputs:</b></td></tr>";
                                foreach (var kvp in flowOutputBindingMapping.Children)
                                {
                                    flowHtml += $"<tr><td><b>{System.Web.HttpUtility.HtmlEncode(kvp.Key.ToString())}:</b> {formatVariable(kvp.Value.ToString())}</td></tr>";
                                }
                                flowHtml += "</table></td></tr>";
                            }
                            flowHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", flowHtml);
                            break;
                        case "InvokeConnector":
                            string connHtml = createCardStart(actionType, !string.IsNullOrEmpty(displayName) ? $"Connector: {displayName}" : "Call a connector");
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("connectionReference"), out var connRefNode))
                            {
                                connHtml += createCardBodyRow($"<b>Connection:</b> {generateMultiLineText(System.Web.HttpUtility.HtmlEncode(connRefNode.ToString()))}");
                            }
                            connHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", connHtml);
                            break;
                        case "EndConversation":
                            actionNode.SetAttributeHtml("label", createCardStart(actionType, !string.IsNullOrEmpty(displayName) ? displayName : "End conversation") + createCardEnd());
                            break;
                        case "EndDialog":
                            actionNode.SetAttributeHtml("label", createCardStart(actionType, !string.IsNullOrEmpty(displayName) ? displayName : "End dialog") + createCardEnd());
                            break;
                        case "OAuthInput":
                            string oauthTitle = "Sign in";
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("title"), out var oauthTitleNode))
                                oauthTitle = oauthTitleNode.ToString();
                            string oauthText = "";
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("text"), out var oauthTextNode))
                                oauthText = oauthTextNode.ToString();
                            string oauthHtml = createCardStart(actionType, $"Sign In: {System.Web.HttpUtility.HtmlEncode(oauthTitle)}");
                            if (!string.IsNullOrEmpty(oauthText))
                                oauthHtml += createCardBodyRow(generateMultiLineText(System.Web.HttpUtility.HtmlEncode(oauthText)));
                            oauthHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", oauthHtml);
                            break;
                        case "SearchAndSummarize":
                            string searchHtml = createCardStart(actionType, !string.IsNullOrEmpty(displayName) ? displayName : "Search and summarize content");
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("variable"), out var searchVarNode))
                            {
                                searchHtml += createCardBodyRow($"<b>Save to:</b> {formatVariable(searchVarNode.ToString())}");
                            }
                            searchHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", searchHtml);
                            break;
                        case "RedirectToTopic":
                            string redirectHeader = !string.IsNullOrEmpty(displayName) ? $"Redirect: {displayName}" : "Redirect to topic";
                            string redirectHtml = createCardStart(actionType, redirectHeader);

                            // Show target dialog/topic for BeginDialog actions
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("dialog"), out var dialogNode))
                            {
                                string dialogRef = dialogNode.ToString();
                                // Extract the topic name from the schema name (e.g. cr6b0_agent.topic.SessionAudit -> SessionAudit)
                                string topicName = dialogRef.Contains('.') ? dialogRef.Substring(dialogRef.LastIndexOf('.') + 1) : dialogRef;
                                redirectHtml += createCardBodyRow($"<table border=\"1\" cellpadding=\"4\"><tr><td><b>Target Topic:</b> {System.Web.HttpUtility.HtmlEncode(topicName)}</td></tr></table>");
                            }

                            // Show target action ID for GotoAction (internal jump)
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("actionId"), out var actionIdNode))
                            {
                                redirectHtml += createCardBodyRow($"<table border=\"1\" cellpadding=\"4\"><tr><td><b>Go to:</b> {System.Web.HttpUtility.HtmlEncode(actionIdNode.ToString())}</td></tr></table>");
                            }

                            // Show input bindings if present (for parameterized topic calls)
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("input"), out var redirectInputNode)
                                && redirectInputNode is YamlMappingNode redirectInputMapping
                                && redirectInputMapping.Children.TryGetValue(new YamlScalarNode("binding"), out var redirectBindingNode)
                                && redirectBindingNode is YamlMappingNode redirectBindingMapping)
                            {
                                redirectHtml += "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\"><tr><td><b>Inputs:</b></td></tr>";
                                foreach (var kvp in redirectBindingMapping.Children)
                                {
                                    redirectHtml += $"<tr><td><b>{System.Web.HttpUtility.HtmlEncode(kvp.Key.ToString())}:</b> {formatVariable(kvp.Value.ToString())}</td></tr>";
                                }
                                redirectHtml += "</table></td></tr>";
                            }

                            // Show output bindings if present
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("output"), out var redirectOutputNode)
                                && redirectOutputNode is YamlMappingNode redirectOutputMapping
                                && redirectOutputMapping.Children.TryGetValue(new YamlScalarNode("binding"), out var redirectOutputBindingNode)
                                && redirectOutputBindingNode is YamlMappingNode redirectOutputBindingMapping)
                            {
                                redirectHtml += "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\"><tr><td><b>Outputs:</b></td></tr>";
                                foreach (var kvp in redirectOutputBindingMapping.Children)
                                {
                                    redirectHtml += $"<tr><td><b>{System.Web.HttpUtility.HtmlEncode(kvp.Key.ToString())}:</b> {formatVariable(kvp.Value.ToString())}</td></tr>";
                                }
                                redirectHtml += "</table></td></tr>";
                            }

                            redirectHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", redirectHtml);
                            break;
                        case "ClearAllVariables":
                            string clearVarsHtml = createCardStart(actionType, !string.IsNullOrEmpty(displayName) ? displayName : "Clear all variables");
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("variables"), out var clearVarsNode))
                            {
                                clearVarsHtml += createCardBodyRow($"<b>Scope:</b> {System.Web.HttpUtility.HtmlEncode(clearVarsNode.ToString())}");
                            }
                            clearVarsHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", clearVarsHtml);
                            break;
                        case "CSATQuestion":
                            string csatHtml = createCardStart(actionType, !string.IsNullOrEmpty(displayName) ? displayName : "Customer satisfaction");
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("conversationOutcome"), out var csatOutcomeNode))
                            {
                                csatHtml += createCardBodyRow($"<b>Conversation outcome:</b> {System.Web.HttpUtility.HtmlEncode(csatOutcomeNode.ToString())}");
                            }
                            csatHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", csatHtml);
                            break;
                        case "HttpRequest":
                            string httpHtml = createCardStart(actionType, !string.IsNullOrEmpty(displayName) ? displayName : "HTTP Request");
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("url"), out var httpUrlNode))
                            {
                                string urlValue = httpUrlNode.ToString();
                                if (urlValue.StartsWith('=')) urlValue = urlValue.Substring(1);
                                httpHtml += createCardBodyRow($"<b>URL:</b><br/>{generateMultiLineText(System.Web.HttpUtility.HtmlEncode(urlValue))}");
                            }
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("response"), out var httpResponseNode))
                            {
                                httpHtml += createCardBodyRow($"<b>Response:</b> {formatVariable(httpResponseNode.ToString())}");
                            }
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("responseSchema"), out var httpSchemaNode)
                                && httpSchemaNode is YamlMappingNode httpSchemaMapping
                                && httpSchemaMapping.Children.TryGetValue(new YamlScalarNode("properties"), out var httpPropsNode)
                                && httpPropsNode is YamlMappingNode httpPropsMapping)
                            {
                                httpHtml += "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\"><tr><td><b>Response Schema:</b></td></tr>";
                                foreach (var prop in httpPropsMapping.Children)
                                {
                                    string propType = prop.Value is YamlScalarNode scalarVal ? scalarVal.ToString() : prop.Value.ToString();
                                    httpHtml += $"<tr><td>{System.Web.HttpUtility.HtmlEncode(prop.Key.ToString())}: {System.Web.HttpUtility.HtmlEncode(propType)}</td></tr>";
                                }
                                httpHtml += "</table></td></tr>";
                            }
                            httpHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", httpHtml);
                            break;
                        case "SetTextVariable":
                            var setTextVarYaml = (YamlScalarNode)((YamlMappingNode)action).Children[new YamlScalarNode("variable")];
                            var setTextValueYaml = (YamlScalarNode)((YamlMappingNode)action).Children[new YamlScalarNode("value")];
                            string setTextVarHtml = createCardStart(actionType, "Set Text Variable");
                            setTextVarHtml += createCardBodyRow($"<table border=\"1\" cellpadding=\"4\"><tr><td>{formatVariable(setTextVarYaml.Value)}</td></tr></table>");
                            setTextVarHtml += createCardBodyRow("To Value");
                            string setTextValue = setTextValueYaml.Value;
                            if (setTextValue.StartsWith('=')) setTextValue = setTextValue.Substring(1);
                            setTextVarHtml += createCardBodyRow($"<table border=\"1\" cellpadding=\"4\"><tr><td>{generateMultiLineText(System.Web.HttpUtility.HtmlEncode(setTextValue))}</td></tr></table>");
                            setTextVarHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", setTextVarHtml);
                            break;
                        case "EditTable":
                            string editTableHtml = createCardStart(actionType, !string.IsNullOrEmpty(displayName) ? displayName : "Edit table");
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("changeType"), out var changeTypeNode))
                            {
                                editTableHtml += createCardBodyRow($"<b>Change type:</b> {System.Web.HttpUtility.HtmlEncode(changeTypeNode.ToString())}");
                            }
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("itemsVariable"), out var itemsVarNode))
                            {
                                editTableHtml += createCardBodyRow($"<b>Table:</b> {formatVariable(itemsVarNode.ToString())}");
                            }
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("value"), out var editTableValueNode))
                            {
                                if (editTableValueNode is YamlMappingNode editTableValueMapping)
                                {
                                    editTableHtml += "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\"><tr><td colspan=\"2\"><b>Value:</b></td></tr>";
                                    foreach (var kvp in editTableValueMapping.Children)
                                    {
                                        string val = kvp.Value.ToString();
                                        editTableHtml += $"<tr><td><b>{System.Web.HttpUtility.HtmlEncode(kvp.Key.ToString())}:</b></td><td>{formatVariable(val)}</td></tr>";
                                    }
                                    editTableHtml += "</table></td></tr>";
                                }
                                else
                                {
                                    string editTableValue = editTableValueNode.ToString();
                                    if (editTableValue.StartsWith('=')) editTableValue = editTableValue.Substring(1);
                                    editTableHtml += createCardBodyRow($"<b>Value:</b><br/>{generateMultiLineText(System.Web.HttpUtility.HtmlEncode(editTableValue))}");
                                }
                            }
                            editTableHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", editTableHtml);
                            break;
                        case "ReplaceDialog":
                            string replaceHeader = !string.IsNullOrEmpty(displayName) ? $"Replace: {displayName}" : "Replace dialog";
                            string replaceHtml = createCardStart(actionType, replaceHeader);
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("dialog"), out var replaceDialogNode))
                            {
                                string replaceDialogRef = replaceDialogNode.ToString();
                                string replaceTopicName = replaceDialogRef.Contains('.') ? replaceDialogRef.Substring(replaceDialogRef.LastIndexOf('.') + 1) : replaceDialogRef;
                                replaceHtml += createCardBodyRow($"<table border=\"1\" cellpadding=\"4\"><tr><td><b>Target Topic:</b> {System.Web.HttpUtility.HtmlEncode(replaceTopicName)}</td></tr></table>");
                            }
                            replaceHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", replaceHtml);
                            break;
                        case "ParseValue":
                            string parseHtml = createCardStart(actionType, !string.IsNullOrEmpty(displayName) ? displayName : "Parse value");
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("variable"), out var parseVarNode))
                            {
                                parseHtml += createCardBodyRow($"<b>Variable:</b> {formatVariable(parseVarNode.ToString())}");
                            }
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("value"), out var parseValueNode))
                            {
                                string parseValue = parseValueNode.ToString();
                                if (parseValue.StartsWith('=')) parseValue = parseValue.Substring(1);
                                parseHtml += createCardBodyRow($"<b>Value:</b> {generateMultiLineText(System.Web.HttpUtility.HtmlEncode(parseValue))}");
                            }
                            if (((YamlMappingNode)action).Children.TryGetValue(new YamlScalarNode("valueType"), out var parseTypeNode)
                                && parseTypeNode is YamlMappingNode parseTypeMapping
                                && parseTypeMapping.Children.TryGetValue(new YamlScalarNode("properties"), out var parsePropsNode)
                                && parsePropsNode is YamlMappingNode parsePropsMapping)
                            {
                                parseHtml += "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\"><tr><td><b>Schema Properties:</b></td></tr>";
                                foreach (var prop in parsePropsMapping.Children)
                                {
                                    string propName = System.Web.HttpUtility.HtmlEncode(prop.Key.ToString());
                                    parseHtml += $"<tr><td>{propName}: {FormatSchemaType(prop.Value)}</td></tr>";
                                }
                                parseHtml += "</table></td></tr>";
                            }
                            parseHtml += createCardEnd();
                            actionNode.SetAttributeHtml("label", parseHtml);
                            break;
                        default:
                            actionNode.SetAttribute("label", CharsetHelper.GetSafeName(actionName));
                            break;
                    }
                    if (conditionCluster == null)
                    {
                        Edge edge = rootGraph.GetOrAddEdge((Node)prevNode, actionNode, actionName);
                    }
                    if (actionType != "ConditionGroup")
                    {
                        prevNode = actionNode;
                    }
                }
            }
            return returnNode;
        }

        /// <summary>
        /// Creates the start of a card-style node label with a colored header bar, similar to Copilot Studio UI.
        /// </summary>
        private string createCardStart(string actionType, string headerText, string iconType = null)
        {
            string borderColor = GraphColours.GetColourForAction(actionType);
            string iconName = iconType ?? actionType;
            return $"<table border=\"0\" cellborder=\"0\" cellspacing=\"5\" cellpadding=\"0\" bgcolor=\"{borderColor}\" style=\"rounded\">"
                 + $"<tr><td bgcolor=\"{borderColor}\" cellpadding=\"8\" align=\"left\">"
                 + $"<table border=\"0\" cellspacing=\"0\" cellpadding=\"2\"><tr>"
                 + $"<td width=\"20\"><img src=\"{folderPath + @"Resources\" + iconName}.png\" /></td>"
                 + $"<td><font color=\"white\"><b>  {headerText}</b></font></td>"
                 + $"<td>   </td>"
                 + $"</tr></table></td></tr>";
        }

        /// <summary>
        /// Creates a body row for the card with padding.
        /// </summary>
        private string createCardBodyRow(string content)
        {
            return $"<tr><td cellpadding=\"8\" align=\"left\" bgcolor=\"white\">{content}</td></tr>";
        }

        /// <summary>
        /// Closes the card table.
        /// </summary>
        private string createCardEnd()
        {
            return "</table>";
        }

        /// <summary>
        /// Formats a variable reference with the (x) badge style, similar to Copilot Studio.
        /// </summary>
        private string formatVariable(string varName)
        {
            return $"<font color=\"#0078d4\"><b>(x)</b></font> {System.Web.HttpUtility.HtmlEncode(varName)}";
        }

        /// <summary>
        /// Formats a YAML schema type node into readable HTML.
        /// Handles scalars ("String"), and mapping nodes with kind/properties
        /// (e.g. {type: {kind: Table, properties: {Name: String, Price: String}}}).
        /// </summary>
        private string FormatSchemaType(YamlNode valueNode)
        {
            if (valueNode is YamlScalarNode scalar)
                return System.Web.HttpUtility.HtmlEncode(scalar.ToString());

            if (valueNode is YamlMappingNode mapping)
            {
                // Check for nested "type" wrapper: { type: { kind: ..., properties: { ... } } }
                if (mapping.Children.TryGetValue(new YamlScalarNode("type"), out var typeNode) && typeNode is YamlMappingNode typeMapping)
                    mapping = typeMapping;

                string kind = null;
                if (mapping.Children.TryGetValue(new YamlScalarNode("kind"), out var kindNode))
                    kind = kindNode.ToString();

                var parts = new List<string>();
                if (!string.IsNullOrEmpty(kind))
                    parts.Add($"<b>{System.Web.HttpUtility.HtmlEncode(kind)}</b>");

                if (mapping.Children.TryGetValue(new YamlScalarNode("properties"), out var propsNode) && propsNode is YamlMappingNode propsMapping)
                {
                    var propNames = new List<string>();
                    foreach (var child in propsMapping.Children)
                    {
                        string childType = FormatSchemaType(child.Value);
                        propNames.Add($"{System.Web.HttpUtility.HtmlEncode(child.Key.ToString())}: {childType}");
                    }
                    if (propNames.Count > 0)
                        parts.Add("{ " + string.Join(", ", propNames) + " }");
                }

                if (parts.Count > 0)
                    return string.Join(" ", parts);
            }

            return System.Web.HttpUtility.HtmlEncode(valueNode.ToString());
        }

        /// <summary>
        /// Formats an entity YAML node into a readable HTML string.
        /// Parses mapping nodes with kind/entityId to show clean entity name and type.
        /// </summary>
        private string formatEntityNode(YamlNode entityNode)
        {
            if (entityNode is YamlMappingNode entityMapping)
            {
                string kind = null;
                string entityId = null;
                entityMapping.Children.TryGetValue(new YamlScalarNode("kind"), out var kindNode);
                entityMapping.Children.TryGetValue(new YamlScalarNode("entityId"), out var entityIdNode);
                kind = kindNode?.ToString();
                entityId = entityIdNode?.ToString();

                if (!string.IsNullOrEmpty(entityId))
                {
                    // Extract the short entity name from e.g. "mcs_ContosoCustomerAssistant.entity.TicketNumber"
                    string displayName = entityId;
                    int lastDot = entityId.LastIndexOf('.');
                    if (lastDot >= 0 && lastDot < entityId.Length - 1)
                        displayName = entityId.Substring(lastDot + 1);

                    string kindLabel = !string.IsNullOrEmpty(kind) ? kind.Replace("EntityReference", "") : null;
                    string result = $"<b>Identify:</b> {System.Web.HttpUtility.HtmlEncode(displayName)}";
                    if (!string.IsNullOrEmpty(kindLabel))
                        result += $" <font point-size=\"9\">({System.Web.HttpUtility.HtmlEncode(kindLabel)})</font>";
                    return result;
                }

                // Handle EmbeddedEntity with inline definition (e.g. ClosedListEntity with items)
                if (kind == "EmbeddedEntity"
                    && entityMapping.Children.TryGetValue(new YamlScalarNode("definition"), out var defNode)
                    && defNode is YamlMappingNode defMapping)
                {
                    string defKind = null;
                    if (defMapping.Children.TryGetValue(new YamlScalarNode("kind"), out var defKindNode))
                        defKind = defKindNode.ToString();

                    string defLabel = !string.IsNullOrEmpty(defKind) ? defKind.Replace("Entity", "") : "Embedded";
                    string result = $"<b>Identify:</b> {System.Web.HttpUtility.HtmlEncode(defLabel)} <font point-size=\"9\">(Embedded)</font>";

                    // List items for ClosedListEntity
                    if (defMapping.Children.TryGetValue(new YamlScalarNode("items"), out var itemsNode)
                        && itemsNode is YamlSequenceNode itemsSeq)
                    {
                        var names = new List<string>();
                        foreach (var item in itemsSeq)
                        {
                            if (item is YamlMappingNode itemMap
                                && itemMap.Children.TryGetValue(new YamlScalarNode("displayName"), out var dnNode))
                                names.Add(System.Web.HttpUtility.HtmlEncode(dnNode.ToString()));
                        }
                        if (names.Count > 0)
                            result += $"<br/>{string.Join(", ", names)}";
                    }
                    return result;
                }

                // Handle other mapping entities with kind but no entityId (e.g. DynamicClosedListEntity)
                if (!string.IsNullOrEmpty(kind))
                {
                    string kindLabel = kind.Replace("Entity", "");
                    string result = $"<b>Identify:</b> {System.Web.HttpUtility.HtmlEncode(kindLabel)}";
                    // Show items source if it's a variable reference
                    if (entityMapping.Children.TryGetValue(new YamlScalarNode("items"), out var dynItemsNode))
                    {
                        string itemsValue = dynItemsNode.ToString();
                        if (itemsValue.StartsWith('='))
                            itemsValue = itemsValue.Substring(1);
                        result += $"<br/><font point-size=\"9\">Items: {formatVariable(itemsValue)}</font>";
                    }
                    return result;
                }
            }
            // Handle scalar prebuilt entity names like "StringPrebuiltEntity", "NumberPrebuiltEntity", etc.
            string entityText = entityNode.ToString();
            if (entityText.EndsWith("PrebuiltEntity"))
            {
                string typeName = entityText.Substring(0, entityText.Length - "PrebuiltEntity".Length);
                return $"<b>Identify:</b> {System.Web.HttpUtility.HtmlEncode(typeName)} <font point-size=\"9\">(Prebuilt)</font>";
            }
            // Fallback: raw display
            return $"<b>Identify:</b> {System.Web.HttpUtility.HtmlEncode(entityText)}";
        }

        /// <summary>
        /// Parses a condition expression and formats it with individual rows and Or/And separators.
        /// </summary>
        private string formatConditionExpression(string conditionString)
        {
            if (conditionString.StartsWith("="))
                conditionString = conditionString.Substring(1).Trim();

            // Try to split by || (OR)
            var orParts = Regex.Split(conditionString, @"\s*\|\|\s*");
            if (orParts.Length > 1)
            {
                string html = "<table border=\"0\" cellpadding=\"3\">";
                for (int i = 0; i < orParts.Length; i++)
                {
                    if (i > 0)
                        html += "<tr><td><b>Or</b></td></tr>";
                    html += $"<tr><td><table border=\"1\" cellpadding=\"4\"><tr><td>{formatSingleCondition(orParts[i].Trim())}</td></tr></table></td></tr>";
                }
                html += "</table>";
                return html;
            }

            // Try && (AND)
            var andParts = Regex.Split(conditionString, @"\s*&&\s*");
            if (andParts.Length > 1)
            {
                string html = "<table border=\"0\" cellpadding=\"3\">";
                for (int i = 0; i < andParts.Length; i++)
                {
                    if (i > 0)
                        html += "<tr><td><b>And</b></td></tr>";
                    html += $"<tr><td><table border=\"1\" cellpadding=\"4\"><tr><td>{formatSingleCondition(andParts[i].Trim())}</td></tr></table></td></tr>";
                }
                html += "</table>";
                return html;
            }

            // Single condition
            return $"<table border=\"1\" cellpadding=\"4\"><tr><td>{formatSingleCondition(conditionString)}</td></tr></table>";
        }

        /// <summary>
        /// Formats a single condition clause with variable badge, operator text, and value.
        /// </summary>
        private string formatSingleCondition(string condition)
        {
            condition = condition.Trim();
            // Try to parse: Variable operator "value" or Variable operator value
            var match = Regex.Match(condition, @"^(.+?)\s*(<>|!=|>=|<=|=|>|<)\s*(.+)$");
            if (match.Success)
            {
                string variable = match.Groups[1].Value.Trim();
                string op = match.Groups[2].Value;
                string value = match.Groups[3].Value.Trim();

                string opText = op switch
                {
                    "=" => "is equal to",
                    "<>" or "!=" => "is not equal to",
                    ">" => "is greater than",
                    "<" => "is less than",
                    ">=" => "is greater than or equal to",
                    "<=" => "is less than or equal to",
                    _ => op
                };

                return $"{formatVariable(variable)}<br/><b><i>{System.Web.HttpUtility.HtmlEncode(opText)}</i></b><br/>{System.Web.HttpUtility.HtmlEncode(value)}";
            }

            return generateMultiLineText(System.Web.HttpUtility.HtmlEncode(condition));
        }

        private string GetActionType(YamlMappingNode action)
        {
            string actionType = ((YamlMappingNode)action).Children[new YamlScalarNode("kind")].ToString();
            switch (actionType)
            {
                case "AdaptiveCardPrompt":
                    return "AdaptiveCard";
                case "ConditionGroup":
                    return "ConditionGroup";
                case "InvokeAIBuilderModelAction":
                    return "AIModel";
                case "SendActivity":
                    return "Message";
                case "SetVariable":
                    return "SetVariable";
                case "LogCustomTelemetryEvent":
                    return "LogCustomTelemetry";
                case "Question":
                    return "Question";
                case "InvokeFlowAction":
                    return "InvokeFlow";
                case "InvokeConnectorAction":
                    return "InvokeConnector";
                case "EndConversation":
                    return "EndConversation";
                case "EndDialog":
                    return "EndDialog";
                case "OAuthInput":
                    return "OAuthInput";
                case "SearchAndSummarizeContent":
                    return "SearchAndSummarize";
                case "GotoAction":
                case "BeginDialog":
                    return "RedirectToTopic";
                case "CancelAllDialogs":
                    return "CancelAllDialogs";
                case "ClearAllVariables":
                    return "ClearAllVariables";
                case "CSATQuestion":
                    return "CSATQuestion";
                case "HttpRequestAction":
                    return "HttpRequest";
                case "SetTextVariable":
                    return "SetTextVariable";
                case "EditTable":
                    return "EditTable";
                case "ReplaceDialog":
                    return "ReplaceDialog";
                case "ParseValue":
                    return "ParseValue";
                default:
                    return actionType;
            }
        }

        private string generateImageFiles(RootGraph rootGraph, bool showSubactions)
        {
            //Generate image files
            string filename = topic.getTopicFileName() + (showSubactions ? "-detailed" : "");
            rootGraph.ToPngFile(folderPath + filename + ".png");
            rootGraph.ToSvgFile(folderPath + filename + ".svg");
            rootGraph.ToDotFile(folderPath + filename + ".dot");
            // Post-process SVG to embed referenced PNG icons as base64 data URIs,
            // so the SVG is self-contained and renders correctly when viewed from HTML pages.
            EmbedImagesInSvg(folderPath + filename + ".svg");
            return filename;
        }

        /// <summary>
        /// Reads an SVG file and replaces all xlink:href/href image references pointing
        /// to local PNG files with inline base64 data URIs.
        /// </summary>
        private static void EmbedImagesInSvg(string svgFilePath)
        {
            if (!File.Exists(svgFilePath)) return;
            string svgContent = File.ReadAllText(svgFilePath);
            // Match xlink:href="..." or href="..." attributes that point to .png files
            string pattern = @"(xlink:href|href)=""([^""]+\.png)""";
            string result = Regex.Replace(svgContent, pattern, match =>
            {
                string attr = match.Groups[1].Value;
                string imagePath = match.Groups[2].Value;
                // Resolve relative paths against the SVG's directory
                if (!Path.IsPathRooted(imagePath))
                {
                    imagePath = Path.Combine(Path.GetDirectoryName(svgFilePath), imagePath);
                }
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

        /// <summary>
        /// Sanitizes a Power Fx expression that produces a JSON object by replacing
        /// string concatenation operators (&amp;) with inlined placeholders so the
        /// result is valid JSON.  e.g. "text" &amp; Topic.Var  →  "text {Topic.Var}"
        /// Also handles unquoted property keys (type: → "type":) and bare Power Fx
        /// expressions as values (items: ForAll(...) → "items": "ForAll(...)").
        /// </summary>
        private static string SanitizePowerFxJson(string input)
        {
            var sb = new StringBuilder(input.Length * 2);
            int i = 0;
            while (i < input.Length)
            {
                char c = input[i];

                if (c == '"')
                {
                    // Handle string literals, including "str" & expr concatenation chains
                    sb.Append(c);
                    i++;
                    while (i < input.Length && input[i] != '"')
                    {
                        if (input[i] == '\\' && i + 1 < input.Length)
                        {
                            sb.Append(input[i++]);
                            sb.Append(input[i++]);
                        }
                        else if (input[i] == '\n') { sb.Append("\\n"); i++; }
                        else if (input[i] == '\r') { sb.Append("\\r"); i++; }
                        else if (input[i] == '\t') { sb.Append("\\t"); i++; }
                        else
                        {
                            sb.Append(input[i++]);
                        }
                    }
                    if (i < input.Length)
                    {
                        // Peek past closing quote for & concatenation
                        int j = i + 1;
                        while (j < input.Length && (input[j] == ' ' || input[j] == '\t'))
                            j++;
                        if (j < input.Length && input[j] == '&')
                        {
                            // Merge chained & expressions/strings into one JSON string
                            j++; // skip &
                            while (j < input.Length && (input[j] == ' ' || input[j] == '\t' || input[j] == '\n' || input[j] == '\r'))
                                j++;
                            while (j < input.Length)
                            {
                                if (input[j] == '"')
                                {
                                    j++; // skip opening quote
                                    while (j < input.Length && input[j] != '"')
                                    {
                                        if (input[j] == '\\' && j + 1 < input.Length)
                                        {
                                            sb.Append(input[j++]);
                                            sb.Append(input[j++]);
                                        }
                                        else if (input[j] == '\n') { sb.Append("\\n"); j++; }
                                        else if (input[j] == '\r') { sb.Append("\\r"); j++; }
                                        else if (input[j] == '\t') { sb.Append("\\t"); j++; }
                                        else { sb.Append(input[j++]); }
                                    }
                                    if (j < input.Length) j++; // skip closing quote
                                }
                                else
                                {
                                    int exprStart = j;
                                    j = ConsumePowerFxExpression(input, j);
                                    string expr = input.Substring(exprStart, j - exprStart).Trim();
                                    sb.Append(" {" + expr.Replace("\"", "\\\"") + "}");
                                }
                                // Check for another &
                                int k = j;
                                while (k < input.Length && (input[k] == ' ' || input[k] == '\t'))
                                    k++;
                                if (k < input.Length && input[k] == '&')
                                {
                                    k++;
                                    while (k < input.Length && (input[k] == ' ' || input[k] == '\t'))
                                        k++;
                                    j = k;
                                }
                                else break;
                            }
                            sb.Append('"');
                            i = j;
                            continue;
                        }
                        sb.Append(input[i++]); // closing quote
                    }
                }
                else if (c == '\'')
                {
                    // Single-quoted string (e.g. '$schema') – convert to double-quoted JSON string
                    sb.Append('"');
                    i++; // skip opening single quote
                    while (i < input.Length && input[i] != '\'')
                    {
                        if (input[i] == '"') { sb.Append("\\\""); i++; }
                        else if (input[i] == '\\' && i + 1 < input.Length) { sb.Append(input[i++]); sb.Append(input[i++]); }
                        else if (input[i] == '\n') { sb.Append("\\n"); i++; }
                        else if (input[i] == '\r') { sb.Append("\\r"); i++; }
                        else { sb.Append(input[i++]); }
                    }
                    if (i < input.Length) i++; // skip closing single quote
                    sb.Append('"');
                }
                else if (char.IsLetter(c) || c == '_' || c == '$')
                {
                    // Could be: unquoted JSON key, JSON keyword (true/false/null), or bare Power Fx expression
                    if (IsFollowedByColon(input, i))
                    {
                        // Unquoted property key – wrap in quotes
                        sb.Append('"');
                        while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_' || input[i] == '$'))
                            sb.Append(input[i++]);
                        sb.Append('"');
                    }
                    else
                    {
                        int identStart = i;
                        int j = i;
                        while (j < input.Length && (char.IsLetterOrDigit(input[j]) || input[j] == '_'))
                            j++;
                        string ident = input.Substring(identStart, j - identStart);
                        if (ident == "true" || ident == "false" || ident == "null")
                        {
                            sb.Append(ident);
                            i = j;
                        }
                        else
                        {
                            // Bare Power Fx expression – consume fully and wrap as a JSON string
                            int exprEnd = ConsumePowerFxExpression(input, i);
                            string expr = input.Substring(i, exprEnd - i).Trim();
                            sb.Append('"');
                            sb.Append(EscapeForJsonString(expr));
                            sb.Append('"');
                            i = exprEnd;
                        }
                    }
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Checks whether the identifier at <paramref name="pos"/> is followed (after optional
        /// whitespace) by a colon, indicating it is an unquoted JSON property key.
        /// </summary>
        private static bool IsFollowedByColon(string input, int pos)
        {
            int j = pos;
            while (j < input.Length && (char.IsLetterOrDigit(input[j]) || input[j] == '_' || input[j] == '$'))
                j++;
            while (j < input.Length && (input[j] == ' ' || input[j] == '\t'))
                j++;
            return j < input.Length && input[j] == ':';
        }

        /// <summary>
        /// Escapes a string for embedding inside a JSON double-quoted string value.
        /// </summary>
        private static string EscapeForJsonString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        /// <summary>
        /// Advances past a Power Fx expression that follows a &amp; concatenation operator.
        /// Handles identifiers, dotted paths, and function calls with nested parens/strings.
        /// </summary>
        private static int ConsumePowerFxExpression(string input, int pos)
        {
            int parenDepth = 0;
            bool inString = false;
            while (pos < input.Length)
            {
                char c = input[pos];
                if (inString)
                {
                    if (c == '\\' && pos + 1 < input.Length) { pos += 2; continue; }
                    if (c == '"') inString = false;
                    pos++;
                    continue;
                }
                if (c == '"') { inString = true; pos++; continue; }
                if (c == '(') { parenDepth++; pos++; continue; }
                if (c == ')')
                {
                    if (parenDepth > 0) { parenDepth--; pos++; }
                    else break;
                    if (parenDepth == 0) break; // end of function call
                    continue;
                }
                // At top level, stop at JSON structural characters or newlines
                if (parenDepth == 0 && (c == ',' || c == '}' || c == ']' || c == '\n' || c == '\r'))
                    break;
                pos++;
            }
            return pos;
        }

        //splits a text into multiple lines (<br/> for line breaks), with each line having a maximum of 65 characters
        private string generateMultiLineText(string text)
        {
            const int maxLineLength = 65;
            string[] words = text.Split(' ');
            string multiLineText = "";
            int lineLength = 0;
            for (var counter = 0; counter < words.Length; counter++)
            {
                string word = words[counter];
                // Force-break words longer than the max line length,
                // but never split in the middle of an HTML entity (e.g. &quot;)
                while (word.Length > maxLineLength)
                {
                    if (lineLength > 0)
                    {
                        multiLineText += "<br/>";
                        lineLength = 0;
                    }
                    int breakAt = maxLineLength;
                    // Check if we're inside an HTML entity (&...;) and adjust break point
                    int ampPos = word.LastIndexOf('&', breakAt - 1);
                    if (ampPos >= 0)
                    {
                        int semiPos = word.IndexOf(';', ampPos);
                        if (semiPos >= breakAt)
                        {
                            // The entity spans the break point; break before the entity instead
                            breakAt = ampPos > 0 ? ampPos : semiPos + 1;
                        }
                    }
                    if (breakAt == 0) breakAt = maxLineLength; // safety fallback
                    multiLineText += word.Substring(0, breakAt) + "<br/>";
                    word = word.Substring(breakAt);
                }
                if (lineLength + word.Length + 1 >= maxLineLength && lineLength > 0)
                {
                    multiLineText += "<br/>";
                    lineLength = 0;
                }
                multiLineText = multiLineText + word + " ";
                lineLength += word.Length + 1;
            }
            return multiLineText;
        }

        /// <summary>
        /// Parses an AdaptiveCard JSON string and returns HTML table rows representing the card body and actions.
        /// Reusable for both the AdaptiveCard action type and Message attachments containing AdaptiveCardTemplate.
        /// </summary>
        private string renderAdaptiveCardContent(string cardSource)
        {
            try
            {
                string cardJsonString = cardSource;
                if (cardJsonString.StartsWith('='))
                    cardJsonString = cardJsonString.Substring(1);

                // Try parsing strategies without throwing: direct parse, then sanitized, then YAML
                JObject cardJson = null;
                try { cardJson = JObject.Parse(cardJsonString); } catch { }

                if (cardJson == null)
                {
                    try { cardJson = JObject.Parse(SanitizePowerFxJson(cardJsonString)); } catch { }
                }

                // Third fallback: parse as YAML (handles unquoted keys and bare Power Fx values natively)
                // then convert to JSON via YamlDotNet's JSON-compatible serializer
                if (cardJson == null)
                {
                    try
                    {
                        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
                        var obj = deserializer.Deserialize(new System.IO.StringReader(cardJsonString));
                        var serializer = new YamlDotNet.Serialization.SerializerBuilder().JsonCompatible().Build();
                        string jsonFromYaml = serializer.Serialize(obj);
                        cardJson = JObject.Parse(jsonFromYaml);
                    }
                    catch { }
                }

                // If all parsing failed, show raw text
                if (cardJson == null)
                {
                    string rawText = cardJsonString;
                    if (rawText.Length > 2000) rawText = rawText.Substring(0, 2000) + "\n...";
                    return createCardBodyRow(generateMultiLineText(System.Web.HttpUtility.HtmlEncode(rawText)));
                }

                // parsing didn't fail, so let's proceed with rendering
                var cardRows = new List<string>();

                if (cardJson["body"] is JArray bodyArray)
                {
                    foreach (var element in bodyArray)
                    {
                        string elementHtml = RenderCardElement(element as JObject);
                        if (!string.IsNullOrEmpty(elementHtml))
                            cardRows.Add($"<tr><td>{elementHtml}</td></tr>");
                    }
                }
                if (cardJson["actions"] is JArray actionsArray)
                {
                    bool hasActions = false;
                    foreach (var cardAction in actionsArray)
                    {
                        string actionHtml = RenderCardAction(cardAction as JObject);
                        if (!string.IsNullOrEmpty(actionHtml))
                        {
                            if (!hasActions) { cardRows.Add("<tr><td> </td></tr>"); hasActions = true; }
                            cardRows.Add($"<tr><td>{actionHtml}</td></tr>");
                        }
                    }
                }

                if (cardRows.Count > 0)
                {
                    return "<tr><td cellpadding=\"8\" bgcolor=\"white\"><table border=\"1\" cellpadding=\"4\">"
                         + string.Join("", cardRows)
                         + "</table></td></tr>";
                }
                return createCardBodyRow("(empty card)");
            }
            catch (Exception)
            {
                string rawText = cardSource;
                if (rawText.StartsWith('=')) rawText = rawText.Substring(1);
                if (rawText.Length > 2000) rawText = rawText.Substring(0, 2000) + "\n...";
                return createCardBodyRow(generateMultiLineText(System.Web.HttpUtility.HtmlEncode(rawText)));
            }
        }

        private string RenderCardElement(JObject element)
        {
            if (element == null) return "";

            string elementType = element["type"]?.ToString() ?? "";
            return elementType switch
            {
                "TextBlock" => RenderTextBlock(element),
                "Input.Text" => RenderInputText(element),
                "Input.ChoiceSet" => RenderInputChoiceSet(element),
                "Input.Date" => RenderInputDate(element),
                "Input.Number" => RenderInputNumber(element),
                "Input.Toggle" => RenderInputToggle(element),
                "Container" => RenderContainer(element),
                "ColumnSet" => RenderColumnSet(element),
                "Image" => RenderImage(element),
                "FactSet" => RenderFactSet(element),
                "ActionSet" => RenderActionSet(element),
                _ => RenderUnsupportedElement(elementType)
            };
        }

        private string RenderUnsupportedElement(string elementType)
        {
            return $"<i>Element: {System.Web.HttpUtility.HtmlEncode(elementType)}(not supported yet)</i>";
        }

        private string RenderCardAction(JObject action)
        {
            if (action == null) return "";

            string actionType = action["type"]?.ToString() ?? "";
            string title = action["title"]?.ToString() ?? "Action";

            return actionType switch
            {
                "Action.Submit" => $"<table border=\"1\" cellpadding=\"4\" bgcolor=\"#f3f2f1\"><tr><td>{System.Web.HttpUtility.HtmlEncode(title)}</td></tr></table>",
                "Action.OpenUrl" => $"<table border=\"1\" cellpadding=\"4\" bgcolor=\"#f3f2f1\"><tr><td>{System.Web.HttpUtility.HtmlEncode(title)}</td></tr></table>",
                "Action.ShowCard" => $"<table border=\"1\" cellpadding=\"4\" bgcolor=\"#f3f2f1\"><tr><td>{System.Web.HttpUtility.HtmlEncode(title)}</td></tr></table>",
                "Action.Execute" => $"<table border=\"1\" cellpadding=\"4\" bgcolor=\"#f3f2f1\"><tr><td>{System.Web.HttpUtility.HtmlEncode(title)}</td></tr></table>",
                "Action.ToggleVisibility" => $"<table border=\"1\" cellpadding=\"4\" bgcolor=\"#f3f2f1\"><tr><td>{System.Web.HttpUtility.HtmlEncode(title)}</td></tr></table>",
                _ => $"<table border=\"1\" cellpadding=\"4\" bgcolor=\"#f3f2f1\"><tr><td>{System.Web.HttpUtility.HtmlEncode(title)}</td></tr></table>"
            };
        }

        private string RenderTextBlock(JObject element)
        {
            string text = element["text"]?.ToString() ?? "";
            bool isSubtle = element["isSubtle"]?.ToObject<bool>() ?? false;
            string weight = element["weight"]?.ToString() ?? "";
            string encodedText = generateMultiLineText(System.Web.HttpUtility.HtmlEncode(text));
            if (weight == "Bolder")
                encodedText = $"<b>{encodedText}</b>";
            if (isSubtle)
                encodedText = $"<i>{encodedText}</i>";
            return encodedText;
        }

        private string RenderInputText(JObject element)
        {
            string id = element["id"]?.ToString() ?? "";
            string placeholder = element["placeholder"]?.ToString() ?? "";
            string label = element["label"]?.ToString() ?? "";
            bool isMultiline = element["isMultiline"]?.ToObject<bool>() ?? false;
            bool isRequired = element["isRequired"]?.ToObject<bool>() ?? false;

            // Graphviz HTML-label cells cannot mix text and <table> children.
            // Wrap everything in a single table so a <td> only ever contains one table.
            string requiredMark = isRequired ? "<font color=\"red\"> *</font>" : "";
            string html = "<table border=\"0\" cellpadding=\"2\">";
            if (!string.IsNullOrEmpty(label))
            {
                html += $"<tr><td>{generateMultiLineText(System.Web.HttpUtility.HtmlEncode(label))}{requiredMark}</td></tr>";
            }
            if (!string.IsNullOrEmpty(placeholder))
            {
                html += $"<tr><td><table border=\"1\" cellpadding=\"4\" color=\"#cccccc\"><tr><td><font color=\"#999999\">{generateMultiLineText(System.Web.HttpUtility.HtmlEncode(placeholder))}</font></td></tr></table></td></tr>";
            }
            html += "</table>";
            return html;
        }

        private string RenderInputChoiceSet(JObject element)
        {
            string id = element["id"]?.ToString() ?? "";
            string label = element["label"]?.ToString() ?? "";
            bool isMultiSelect = element["isMultiSelect"]?.ToObject<bool>() ?? false;

            string choicesText = "";
            string indicator = isMultiSelect ? "[ ]" : "( )";
            if (element["choices"] is JArray choices)
            {
                var choiceList = new List<string>();
                foreach (var choice in choices)
                {
                    string title = choice["title"]?.ToString() ?? "";
                    choiceList.Add($"  {indicator} {System.Web.HttpUtility.HtmlEncode(title)}");
                }
                choicesText = string.Join("<br/>", choiceList);
            }

            bool isRequired = element["isRequired"]?.ToObject<bool>() ?? false;
            string requiredMark = isRequired ? "<font color=\"red\"> *</font>" : "";
            string selectLabel = !string.IsNullOrEmpty(label) ? $"{generateMultiLineText(System.Web.HttpUtility.HtmlEncode(label))}{requiredMark}" : "";
            return $"{selectLabel}<br/>{choicesText}";
        }

        private string RenderInputDate(JObject element)
        {
            string id = element["id"]?.ToString() ?? "";
            string label = element["label"]?.ToString() ?? "";
            string min = element["min"]?.ToString() ?? "";
            string max = element["max"]?.ToString() ?? "";

            string constraints = "";
            if (!string.IsNullOrEmpty(min) || !string.IsNullOrEmpty(max))
            {
                constraints = $"<br/>Range: {min} to {max}";
            }

            return $"<b>Date Input:</b> {label} (ID: {id}){constraints}";
        }

        private string RenderInputNumber(JObject element)
        {
            string id = element["id"]?.ToString() ?? "";
            string label = element["label"]?.ToString() ?? "";
            string min = element["min"]?.ToString() ?? "";
            string max = element["max"]?.ToString() ?? "";

            string constraints = "";
            if (!string.IsNullOrEmpty(min) || !string.IsNullOrEmpty(max))
            {
                constraints = $"<br/>Range: {min} to {max}";
            }

            return $"<b>Number Input:</b> {label} (ID: {id}){constraints}";
        }

        private string RenderInputToggle(JObject element)
        {
            string id = element["id"]?.ToString() ?? "";
            string title = element["title"]?.ToString() ?? "";
            string valueOn = element["valueOn"]?.ToString() ?? "true";
            string valueOff = element["valueOff"]?.ToString() ?? "false";

            return $"<b>Toggle:</b> {title} (ID: {id})<br/>Values: {valueOff}/{valueOn}";
        }

        private string RenderContainer(JObject element)
        {
            var itemsToken = element["items"];
            if (itemsToken is JArray items)
            {
                var childHtml = new List<string>();
                foreach (var item in items)
                {
                    string html = RenderCardElement(item as JObject);
                    if (!string.IsNullOrEmpty(html))
                        childHtml.Add($"<tr><td>{html}</td></tr>");
                }
                if (childHtml.Count > 0)
                    return $"<table border=\"0\" cellpadding=\"2\">{string.Join("", childHtml)}</table>";
                return "(empty container)";
            }
            // items is a string (e.g. sanitized ForAll expression)
            if (itemsToken != null)
            {
                string expr = itemsToken.ToString();
                var rows = new List<string>();

                // Try to extract and render an embedded JSON object from the expression
                int braceStart = expr.IndexOf('{');
                int braceEnd = expr.LastIndexOf('}');
                if (braceStart >= 0 && braceEnd > braceStart)
                {
                    // Show only the prefix before the adaptive card (e.g. "ForAll(Topic.OrderDetails, ")
                    string prefix = expr.Substring(0, braceStart).Trim();
                    if (!string.IsNullOrEmpty(prefix))
                        rows.Add($"<tr><td><i>{generateMultiLineText(System.Web.HttpUtility.HtmlEncode(prefix))}</i></td></tr>");

                    string templateJson = expr.Substring(braceStart, braceEnd - braceStart + 1);
                    JObject templateObj = null;
                    try { templateObj = JObject.Parse(templateJson); } catch { }
                    if (templateObj == null)
                    {
                        try { templateObj = JObject.Parse(SanitizePowerFxJson(templateJson)); } catch { }
                    }
                    if (templateObj != null)
                    {
                        string rendered = RenderCardElement(templateObj);
                        if (!string.IsNullOrEmpty(rendered))
                            rows.Add($"<tr><td><table border=\"1\" cellpadding=\"4\" color=\"#cccccc\">"
                                   + $"<tr><td>{rendered}</td></tr></table></td></tr>");
                    }
                }
                else
                {
                    // No embedded JSON found – show the full expression as fallback
                    rows.Add($"<tr><td><i>{generateMultiLineText(System.Web.HttpUtility.HtmlEncode(expr))}</i></td></tr>");
                }

                return $"<table border=\"0\" cellpadding=\"2\">{string.Join("", rows)}</table>";
            }
            return "(empty container)";
        }

        private string RenderColumnSet(JObject element)
        {
            if (element["columns"] is JArray columns)
            {
                var colHtml = new List<string>();
                foreach (var col in columns)
                {
                    if (col is JObject colObj)
                    {
                        var cellRows = new List<string>();
                        if (colObj["items"] is JArray colItems)
                        {
                            foreach (var item in colItems)
                            {
                                string html = RenderCardElement(item as JObject);
                                if (!string.IsNullOrEmpty(html))
                                    cellRows.Add($"<tr><td>{html}</td></tr>");
                            }
                        }
                        string content = cellRows.Count > 0
                            ? $"<table border=\"0\" cellpadding=\"1\">{string.Join("", cellRows)}</table>"
                            : "(empty)";
                        colHtml.Add($"<td cellpadding=\"4\" valign=\"top\">{content}</td>");
                    }
                }
                if (colHtml.Count > 0)
                    return $"<table border=\"0\" cellpadding=\"2\"><tr>{string.Join("", colHtml)}</tr></table>";
            }
            return "<b>Column Set</b> (empty)";
        }

        private string RenderImage(JObject element)
        {
            string url = element["url"]?.ToString() ?? "";
            string altText = element["altText"]?.ToString() ?? "";

            // Graphviz <img> only supports local file paths, not remote URLs.
            // Show alt text and a truncated URL as a text-only placeholder.
            string label = !string.IsNullOrEmpty(altText)
                ? System.Web.HttpUtility.HtmlEncode(altText)
                : "Image";
            string urlHint = "";
            if (!string.IsNullOrEmpty(url))
            {
                string displayUrl = url.Length > 50 ? url.Substring(0, 50) + "..." : url;
                urlHint = $"<br/>{System.Web.HttpUtility.HtmlEncode(displayUrl)}";
            }
            return $"[img] <b>{label}</b>{urlHint}";
        }

        private string RenderFactSet(JObject element)
        {
            int factCount = 0;
            if (element["facts"] is JArray facts)
            {
                factCount = facts.Count;
                var factList = new List<string>();
                foreach (var fact in facts)
                {
                    string title = fact["title"]?.ToString() ?? "";
                    string value = fact["value"]?.ToString() ?? "";
                    factList.Add($"<b>{System.Web.HttpUtility.HtmlEncode(title)}</b> {generateMultiLineText(System.Web.HttpUtility.HtmlEncode(value))}");
                }
                string factText = string.Join("<br/>", factList);
                return factText;
            }

            return $"<b>Fact Set</b> with {factCount} fact(s)";
        }

         private string RenderActionSet(JObject element)
        {
            if (element["actions"] is JArray actions)
            {
                var buttons = new List<string>();
                foreach (var action in actions)
                {
                    string html = RenderCardAction(action as JObject);
                    if (!string.IsNullOrEmpty(html))
                        buttons.Add($"<tr><td>{html}</td></tr>");
                }
                if (buttons.Count > 0)
                    return $"<table border=\"0\" cellpadding=\"2\">{string.Join("", buttons)}</table>";
            }
            return "<b>Action Set</b> (empty)";
        }
        // Add this method to create constraint edges between parallel nodes
        //TODO potentially no longer required
        private void CreateLevelConstraints(RootGraph rootGraph, SubGraph cluster, List<Node> parallelNodes)
        {
            if (parallelNodes.Count <= 1) return;
            Console.WriteLine(topic.Name);
            foreach (Node node in parallelNodes)
            {
                Console.WriteLine("Parallel Node: " + node.GetName());
            }
            Console.WriteLine();
            // Create invisible edges between parallel nodes to maintain same level
            for (int i = 0; i < parallelNodes.Count - 1; i++)
            {
                Edge constraintEdge = rootGraph.GetOrAddEdge(parallelNodes[i], parallelNodes[i + 1], "constraint_" + i);
                constraintEdge.SetAttribute("style", "invis");
                constraintEdge.SetAttribute("constraint", "false"); // Don't affect layout, just ranking
                constraintEdge.SetAttribute("weight", "0");
            }
            // Create a rank subgraph
            string rankName = "rank_" + parallelNodes[0].GetName() + "_group";
            SubGraph rankGroup = cluster.GetOrAddSubgraph(rankName);
            rankGroup.SetAttribute("rank", "same");
            rankGroup.SetAttribute("style", "invis");

            foreach (var node in parallelNodes)
            {
                rankGroup.AddExisting(node);
            }
        }

        /// <summary>
        /// Generates an agent-level topic data flow diagram showing how topics call each other
        /// (BeginDialog/ReplaceDialog) with inputType/outputType schemas on each node.
        /// </summary>
        internal static void BuildTopicDataFlowGraph(AgentEntity agent, AgentDocumentationContent content, string folderPath)
        {
            var topics = agent.GetTopics();
            var calls = content.GetTopicDataFlowInfo();

            // Only generate if there are topic-to-topic calls
            if (calls.Count == 0) return;

            RootGraph rootGraph = RootGraph.CreateNew(GraphType.Directed, "TopicDataFlow_" + CharsetHelper.GetSafeName(agent.Name));
            Graph.IntroduceAttribute(rootGraph, "rankdir", "LR");
            Graph.IntroduceAttribute(rootGraph, "fontname", "helvetica");
            Graph.IntroduceAttribute(rootGraph, "nodesep", "0.8");
            Graph.IntroduceAttribute(rootGraph, "ranksep", "1.2");
            Node.IntroduceAttribute(rootGraph, "shape", "plain");
            Node.IntroduceAttribute(rootGraph, "fontname", "helvetica");
            Edge.IntroduceAttribute(rootGraph, "fontname", "helvetica");
            Edge.IntroduceAttribute(rootGraph, "fontsize", "9");
            Edge.IntroduceAttribute(rootGraph, "label", "");
            Edge.IntroduceAttribute(rootGraph, "style", "");
            Edge.IntroduceAttribute(rootGraph, "color", "");

            // Collect all topics referenced in calls
            var involvedSchemaNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var call in calls)
            {
                involvedSchemaNames.Add(call.SourceTopicSchemaName);
                involvedSchemaNames.Add(call.TargetTopicSchemaName);
            }

            // System topic prefixes (muted color)
            var systemTopicNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Greeting", "Goodbye", "ThankYou", "Fallback", "MultipleTopicsMatched",
                "Escalate", "OnError", "ResetConversation", "StartOver", "Signin",
                "ConversationStart", "Search"
            };

            // Create nodes for involved topics
            var nodeMap = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            foreach (var topic in topics)
            {
                if (!involvedSchemaNames.Contains(topic.SchemaName)) continue;

                string nodeId = CharsetHelper.GetSafeName(topic.SchemaName);
                Node n = rootGraph.GetOrAddNode(nodeId);
                nodeMap[topic.SchemaName] = n;

                string triggerType = topic.GetTriggerTypeForTopic();
                var inputProps = topic.GetInputTypeProperties();
                var outputProps = topic.GetOutputTypeProperties();

                bool isSystem = systemTopicNames.Contains(topic.getTopicFileName());
                string headerColor = isSystem ? "#999999" : "#0078d4";
                string fillColor = isSystem ? "#f0f0f0" : "#e7f3ff";

                // Build HTML-like label (SetAttributeHtml wraps in < > so we omit outer brackets)
                var sb = new StringBuilder();
                sb.Append($"<TABLE BORDER=\"0\" CELLBORDER=\"1\" CELLSPACING=\"0\" CELLPADDING=\"4\">");

                // Header row
                sb.Append($"<TR><TD COLSPAN=\"2\" BGCOLOR=\"{headerColor}\" ALIGN=\"CENTER\">");
                sb.Append($"<FONT COLOR=\"white\"><B>{System.Security.SecurityElement.Escape(topic.Name)}</B></FONT>");
                sb.Append("</TD></TR>");

                // Trigger type row
                sb.Append($"<TR><TD COLSPAN=\"2\" BGCOLOR=\"{fillColor}\" ALIGN=\"CENTER\">");
                sb.Append($"<FONT POINT-SIZE=\"9\">{System.Security.SecurityElement.Escape(triggerType)}</FONT>");
                sb.Append("</TD></TR>");

                // Input/Output properties side by side
                int maxRows = Math.Max(inputProps.Count, outputProps.Count);
                if (maxRows > 0)
                {
                    // Section header
                    sb.Append($"<TR>");
                    sb.Append($"<TD BGCOLOR=\"#f5f5f5\" ALIGN=\"CENTER\"><FONT POINT-SIZE=\"8\"><B>Inputs</B></FONT></TD>");
                    sb.Append($"<TD BGCOLOR=\"#f5f5f5\" ALIGN=\"CENTER\"><FONT POINT-SIZE=\"8\"><B>Outputs</B></FONT></TD>");
                    sb.Append("</TR>");

                    for (int i = 0; i < maxRows; i++)
                    {
                        sb.Append("<TR>");
                        if (i < inputProps.Count)
                            sb.Append($"<TD ALIGN=\"LEFT\"><FONT POINT-SIZE=\"8\">{System.Security.SecurityElement.Escape(inputProps[i].Name)}: {System.Security.SecurityElement.Escape(inputProps[i].Type)}</FONT></TD>");
                        else
                            sb.Append("<TD></TD>");

                        if (i < outputProps.Count)
                            sb.Append($"<TD ALIGN=\"LEFT\"><FONT POINT-SIZE=\"8\">{System.Security.SecurityElement.Escape(outputProps[i].Name)}: {System.Security.SecurityElement.Escape(outputProps[i].Type)}</FONT></TD>");
                        else
                            sb.Append("<TD></TD>");
                        sb.Append("</TR>");
                    }
                }

                sb.Append("</TABLE>");
                n.SetAttributeHtml("label", sb.ToString());
            }

            // Also add nodes for targets that aren't in the agent's topics (external references)
            foreach (var call in calls)
            {
                if (!nodeMap.ContainsKey(call.TargetTopicSchemaName))
                {
                    string nodeId = CharsetHelper.GetSafeName(call.TargetTopicSchemaName);
                    Node n = rootGraph.GetOrAddNode(nodeId);
                    nodeMap[call.TargetTopicSchemaName] = n;
                    string label = $"<TABLE BORDER=\"0\" CELLBORDER=\"1\" CELLSPACING=\"0\" CELLPADDING=\"4\">"
                        + $"<TR><TD BGCOLOR=\"#cccccc\" ALIGN=\"CENTER\"><FONT COLOR=\"white\"><B>{System.Security.SecurityElement.Escape(call.TargetTopicName)}</B></FONT></TD></TR>"
                        + "</TABLE>";
                    n.SetAttributeHtml("label", label);
                }
            }

            // Create edges
            foreach (var call in calls)
            {
                if (!nodeMap.TryGetValue(call.SourceTopicSchemaName, out Node srcNode)) continue;
                if (!nodeMap.TryGetValue(call.TargetTopicSchemaName, out Node tgtNode)) continue;

                Edge edge = rootGraph.GetOrAddEdge(srcNode, tgtNode, call.SourceTopicSchemaName + "->" + call.TargetTopicSchemaName + "_" + call.CallKind);

                // Build edge label from bindings
                var bindingParts = new List<string>();
                foreach (var kvp in call.InputBindings)
                {
                    bindingParts.Add($"{kvp.Key}");
                }
                if (bindingParts.Count > 0)
                {
                    edge.SetAttribute("label", string.Join("\\n", bindingParts));
                }

                if (call.CallKind == "ReplaceDialog")
                {
                    edge.SetAttribute("style", "dashed");
                    edge.SetAttribute("color", "#0077ff");
                }
                else
                {
                    edge.SetAttribute("color", "#333333");
                }
            }

            rootGraph.CreateLayout();
            string filename = "topic-dataflow";
            rootGraph.ToPngFile(folderPath + filename + ".png");
            rootGraph.ToSvgFile(folderPath + filename + ".svg");
            rootGraph.ToDotFile(folderPath + filename + ".dot");
            EmbedImagesInSvg(folderPath + filename + ".svg");
            NotificationHelper.SendNotification("  - Created Topic Data Flow Graph");
        }
    }

    public static class GraphColours
    {
        public static string TriggerColour = "#0077ff";
        public static string TriggerFillColour = "#e7f4ff";
        public static string SetVariableFillColour = "#e7f4ff";
        public static string SetVariableColour = "#118dff";
        public static string ConditionFillColour = "#e7f4ff";
        public static string ConditionColour = "#118dff";
        public static string MessageColour = "#672367";
        public static string MessageFillColour = "#f0e9f0";
        public static string AdaptiveCardColour = "#672367";
        public static string AdaptiveCardFillColour = "#f0e9f0";
        public static string AIModelCardColour = "#672367";
        public static string AIModelCardFillColour = "#f0e9f0";
        public static string CancelAllDialogsColour = "#6bb700";
        public static string CancelAllDialogsFillColour = "#f0f8e6";
        public static string LogCustomTelemetryColour = "#242424";
        public static string LogCustomTelemetryFillColour = "#edebe9";
        public static string QuestionColour = "#672367";
        public static string QuestionFillColour = "#f0e9f0";
        public static string InvokeFlowColour = "#0078d4";
        public static string InvokeFlowFillColour = "#e5f1fb";
        public static string InvokeConnectorColour = "#0078d4";
        public static string InvokeConnectorFillColour = "#e5f1fb";
        public static string EndConversationColour = "#6bb700";
        public static string EndConversationFillColour = "#f0f8e6";
        public static string EndDialogColour = "#6bb700";
        public static string EndDialogFillColour = "#f0f8e6";
        public static string OAuthInputColour = "#0078d4";
        public static string OAuthInputFillColour = "#e5f1fb";
        public static string SearchAndSummarizeColour = "#0078d4";
        public static string SearchAndSummarizeFillColour = "#e5f1fb";
        public static string RedirectToTopicColour = "#0077ff";
        public static string RedirectToTopicFillColour = "#e7f4ff";
        public static string ClearAllVariablesColour = "#118dff";
        public static string ClearAllVariablesFillColour = "#e7f4ff";
        public static string CSATQuestionColour = "#672367";
        public static string CSATQuestionFillColour = "#f0e9f0";
        public static string HttpRequestColour = "#0078d4";
        public static string HttpRequestFillColour = "#e5f1fb";
        public static string SetTextVariableColour = "#118dff";
        public static string SetTextVariableFillColour = "#e7f4ff";
        public static string EditTableColour = "#118dff";
        public static string EditTableFillColour = "#e7f4ff";
        public static string ReplaceDialogColour = "#0077ff";
        public static string ReplaceDialogFillColour = "#e7f4ff";
        public static string ParseValueColour = "#118dff";
        public static string ParseValueFillColour = "#e7f4ff";

        public static string GetColourForAction(string actionType)
        {
            return actionType switch
            {
                "Trigger" => TriggerColour,
                "Message" => MessageColour,
                "Question" => QuestionColour,
                "CancelAllDialogs" => CancelAllDialogsColour,
                "SetVariable" => SetVariableColour,
                "ConditionGroup" => ConditionColour,
                "LogCustomTelemetry" => LogCustomTelemetryColour,
                "AdaptiveCard" => AdaptiveCardColour,
                "AIModel" => AIModelCardColour,
                "InvokeFlow" => InvokeFlowColour,
                "InvokeConnector" => InvokeConnectorColour,
                "EndConversation" => EndConversationColour,
                "EndDialog" => EndDialogColour,
                "OAuthInput" => OAuthInputColour,
                "SearchAndSummarize" => SearchAndSummarizeColour,
                "RedirectToTopic" => RedirectToTopicColour,
                "ClearAllVariables" => ClearAllVariablesColour,
                "CSATQuestion" => CSATQuestionColour,
                "HttpRequest" => HttpRequestColour,
                "SetTextVariable" => SetTextVariableColour,
                "EditTable" => EditTableColour,
                "ReplaceDialog" => ReplaceDialogColour,
                "ParseValue" => ParseValueColour,
                _ => "black",
            };
        }

        public static string GetFillColourForAction(string actionType)
        {
            string colour = actionType switch
            {
                "Trigger" => TriggerFillColour,
                "Message" => MessageFillColour,
                "Question" => QuestionFillColour,
                "CancelAllDialogs" => CancelAllDialogsFillColour,
                "SetVariable" => SetVariableFillColour,
                "ConditionGroup" => ConditionFillColour,
                "LogCustomTelemetry" => LogCustomTelemetryFillColour,
                "AdaptiveCard" => AdaptiveCardFillColour,
                "AIModel" => AIModelCardFillColour,
                "InvokeFlow" => InvokeFlowFillColour,
                "InvokeConnector" => InvokeConnectorFillColour,
                "EndConversation" => EndConversationFillColour,
                "EndDialog" => EndDialogFillColour,
                "OAuthInput" => OAuthInputFillColour,
                "SearchAndSummarize" => SearchAndSummarizeFillColour,
                "RedirectToTopic" => RedirectToTopicFillColour,
                "ClearAllVariables" => ClearAllVariablesFillColour,
                "CSATQuestion" => CSATQuestionFillColour,
                "HttpRequest" => HttpRequestFillColour,
                "SetTextVariable" => SetTextVariableFillColour,
                "EditTable" => EditTableFillColour,
                "ReplaceDialog" => ReplaceDialogFillColour,
                "ParseValue" => ParseValueFillColour,
                _ => "red",
            };
            return colour;
        }
    }
}
