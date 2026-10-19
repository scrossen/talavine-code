namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Standard function types for AI model routing.
/// Applications can extend this with their own function types.
/// </summary>
public static class FunctionTypes
{
    /// <summary>Bounded tool-free preparation research and the matching source-grounded preparation contract.</summary>
    public const string HarnessLearning = "harness-learning";
    // Core AI operations
    public const string Chatbot = "chatbot";
    public const string DataAnalysis = "data-analysis";
    public const string ContentGeneration = "content-generation";
    /// <summary>
    /// Typed conversation used to explain and propose edits to a supervised
    /// agent's operating manual. Kept separate from general content generation
    /// so validated JSON capability evidence can be reused safely.
    /// </summary>
    public const string AgentInstructionChat = "agent-instruction-chat";
    /// <summary>
    /// Typed Vibes blueprint generation used by both create and natural-language
    /// edit flows. The response is parsed and verified before it can become an app.
    /// </summary>
    public const string VibeBuilder = "vibe-builder";
    public const string SentimentAnalysis = "sentiment-analysis";
    public const string Classification = "classification";
    public const string Translation = "translation";
    public const string Summarization = "summarization";
    public const string CodeGeneration = "code-generation";
    public const string DocumentProcessing = "document-processing";
    public const string Vision = "vision";
    public const string VisualAnalysis = "visual-analysis";
    public const string CaptchaSolving = "captcha-solving";
    public const string Default = "default";

    // Support ticket operations
    public const string Autoresponder = "autoresponder";
    public const string TicketSummary = "ticket-summary";
    public const string TicketResponse = "ticket-response";
    public const string TicketAnalysis = "ticket-analysis";

    // Email operations
    public const string EmailProcessing = "email-processing";

    /// <summary>
    /// One consolidated LLM call per new message. Writes threat classification,
    /// inbox routing, category, summary, sentiment, entities, action items,
    /// and the knowledge-worthy verdict in a single pass. Replaces the
    /// historical six separate per-message calls (summary, category,
    /// sentiment, entity extraction, spam detection, inbox policy). Kept as
    /// its own function type so the user can route this one expensive pass
    /// at a stronger model than the narrow routes — e.g. Sonnet/Opus for
    /// enrichment while keeping cheap Haiku-class models for the rest.
    /// </summary>
    public const string MessagePipeline = "message-pipeline";

    /// <summary>
    /// Local-only background pass that divides a deterministic chat window into
    /// coherent topic segments and supplies a short subject and summary for each.
    /// Thinking must stay disabled for this structured-output workload.
    /// </summary>
    public const string ChatSegmentation = "chat-segmentation";

    // Knowledge base operations
    public const string KnowledgeBaseGeneration = "knowledge-base-generation";
    public const string KnowledgeRetrieval = "knowledge-retrieval";
    public const string KnowledgeExtraction = "knowledge-extraction";

    // Task and workflow operations
    public const string TaskCreation = "task-creation";
    public const string WorkflowPlanning = "workflow-planning";
    public const string WorkflowVerification = "workflow-verification";

    // Learning and optimization
    public const string FeedbackAnalysis = "feedback-analysis";
    public const string SystemOptimization = "system-optimization";
    public const string PromptOptimization = "prompt-optimization";
    public const string ContextAnalysis = "context-analysis";
    public const string LearningAnalysis = "learning-analysis";

    // CRM operations
    public const string TerritoryAssignment = "territory-assignment";
    public const string FAQAnswering = "faq-answering";
    public const string ContactSummary = "contact-summary";
    public const string LeadAnalysis = "lead-analysis";
    public const string CrmAnalysis = "crm-analysis";
    public const string ContactListViewFilterGeneration = "contact-list-view-filter-generation";
    /// <summary>Proposes an editable business setup plan; never supplies account authority or consent.</summary>
    public const string BusinessSetupPlanning = "business-setup-planning";
    /// <summary>Source-backed business summary and explicitly proposed customer audiences.</summary>
    public const string BusinessProspectingPlanning = "business-prospecting-planning";
    /// <summary>Draft-only personalization grounded in the reviewed offer and attributed research.</summary>
    public const string BusinessProspectingDrafting = "business-prospecting-drafting";

    // Embedding operations
    public const string TextEmbedding = "text-embedding";
    public const string ImageEmbedding = "image-embedding";

    // Institutional Knowledge operations
    public const string DecisionExtraction = "decision-extraction";
    public const string ActionItemExtraction = "action-item-extraction";
    public const string ConversationAnalysis = "conversation-analysis";
    public const string InstitutionalKnowledgeQuery = "institutional-knowledge-query";

    // Error Analysis operations
    public const string ErrorAnalysis = "error-analysis";
    public const string ErrorDigest = "error-digest";
    public const string SystemHealthAssessment = "system-health-assessment";

    // Alert/Monitoring operations
    public const string AlertAnalysis = "alert-analysis";
    public const string AlertSummary = "alert-summary";

    // Spam detection
    public const string SpamDetection = "spam-detection";

    // Troubleshooting
    public const string Troubleshooting = "troubleshooting";

    // Test data generation
    public const string TestDataGeneration = "test-data-generation";

    // Admin/Test operations
    public const string AdminTest = "admin-test";

    /// <summary>
    /// One LLM call per iteration of the goal-pursuing agent loop. This is the
    /// model that picks tools, reads prior failures, and decides when to stop.
    /// Route this to a strong reasoning model.
    /// </summary>
    public const string AgentLoop = "agent-loop";

    /// <summary>
    /// One LLM call at the start of each chat turn to pick which agent should
    /// handle the request. Sees a short list of agent descriptions and returns
    /// a single agentId. Replaces the old keyword-based confidence ranker.
    /// Should be a fast, cheap model — the prompt is small and the response is
    /// just an id, so latency dominates throughput here.
    /// </summary>
    public const string AgentSelection = "agent-selection";

    /// <summary>
    /// Unified agent chat turn with native tool-use. This is the function type
    /// for the tool-use engine that replaces the old AgentLoop + AgentSelection
    /// pair. Route to a strong reasoning model that supports native tool-use
    /// (e.g. Claude Opus/Sonnet, GPT-4/4o, Llama 3.1+ via Ollama).
    /// </summary>
    public const string AgentChat = "agent-chat";

    /// <summary>
    /// A coding-harness turn in a host repository workspace: the model edits,
    /// searches, builds and commits through tools. Hosts rank candidate routes
    /// for this function to build their coding lanes (tool use is required), so
    /// coding work can be routed and benchmarked separately from ordinary
    /// assistant chat (<see cref="AgentChat"/>).
    /// </summary>
    public const string CodingAssistant = "coding-assistant";
}
