using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Dashboard;
using AniRuntime.Emergence;
using AniRuntime.Emergence.Models;
using AniRuntime.LLM;
using AniRuntime.Loops;
using AniRuntime.Loops.Coreference;
using AniRuntime.Loops.Invariants;
using AniRuntime.Loops.Pipeline;
using AniRuntime.Memory;
using AniRuntime.Perception;
using AniRuntime.Voice;
using LearnedGeek.ML;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Service;

/// <summary>
/// Centralized service registration for the ANI Runtime composition root.
/// Extracted from <c>Program.cs</c> on 2026-06-02 so a sibling eval-driver
/// host (<c>tools/AniRuntime.Eval/</c>, Issue #79) can reuse the same DI
/// graph without duplicating ~500 lines of registration code.
///
/// <para>
/// Scope: every service the cognitive cycle needs to function — options,
/// memory, LLM, perception sources, cognitive pipeline + invariants,
/// reply channels, hosted services, dashboard, emergence. ASP.NET-specific
/// concerns (rate limiter, forwarded headers, Kestrel, webhook endpoints)
/// remain in the consuming host's startup code.
/// </para>
///
/// <para>
/// Hosted services (<c>AniHeartbeatService</c>, <c>DiagnosticScheduler</c>)
/// ARE registered here. They only activate when the consuming host
/// implements <c>IHost</c> and calls <c>StartAsync</c>. A plain
/// <c>ServiceProvider</c> built directly from <c>IServiceCollection</c>
/// (as the eval driver will use) registers them but never starts them.
/// </para>
/// </summary>
public static class AniRuntimeServiceContainer
{
    public static void AddAniRuntimeCore(IServiceCollection services, IConfiguration config)
    {
        // ── Options (deferred reads — all resolved after Build()) ─────────────────
        services.Configure<AniOptions>(config.GetSection("Ani"));
        services.Configure<OllamaOptions>(config.GetSection("Ollama"));
        services.Configure<TwilioOptions>(config.GetSection("Twilio"));
        services.Configure<RssOptions>(config.GetSection("Rss"));
        services.Configure<WeatherOptions>(config.GetSection("Weather"));
        services.Configure<VoiceOptions>(config.GetSection("Voice"));
        services.Configure<ImageOptions>(config.GetSection("Images"));
        services.Configure<EmergenceOptions>(config.GetSection("Emergence"));
        services.Configure<DiagnosticOptions>(config.GetSection("Diagnostic"));
        // Theme P Phase P.1 (May 11, 2026) — cross-class verifier config.
        // Real API key set in appsettings.Development.json on ani-server
        // (committed config carries a placeholder). See plan-doc §4 lock 7
        // for the FrontierVerifierEnabled emergency-kill semantics.
        services.Configure<AnthropicOptions>(config.GetSection("Anthropic"));

        // ── Core memory services (Phase 5 closeout — legacy deleted 2026-05-18) ──
        // EfMemoryServiceFacade implements the composite IMemoryService by
        // delegating each method to one of five focused services (one per
        // ISP-split interface). Four named domain services (MemoryAuditWriter,
        // MemoryMergePolicy, MemoryLinkRebuilder, SemanticSearchComposer) own
        // the cross-cutting behaviours that used to be private helpers on the
        // SqliteMemoryService monolith.

        // Domain services — referenced by the focused services below.
        services.AddSingleton<IMemoryAuditWriter, EfMemoryAuditWriter>();
        services.AddSingleton<IMemoryMergePolicy, EfMemoryMergePolicy>();
        services.AddSingleton<IMemoryLinkRebuilder, EfMemoryLinkRebuilder>();
        services.AddSingleton<ISemanticSearchComposer, EfSemanticSearchComposer>();

        // Five focused services + composite façade.
        services.AddSingleton<IMemoryPersistence, EfMemoryPersistenceService>();
        services.AddSingleton<IMemorySearch, EfMemorySearchService>();
        services.AddSingleton<IStateStore, EfStateStore>();
        services.AddSingleton<IMemoryAnalytics, EfMemoryAnalyticsService>();
        services.AddSingleton<IMemoryMaintenance, EfMemoryMaintenanceService>();
        services.AddSingleton<IMemoryService, EfMemoryServiceFacade>();
        services.AddSingleton<IConversationService, SqliteConversationService>();

        // ── Phase 3 data-layer refactor (May 17, 2026): EF Core context factory ──
        // + atomic reflection-gist service. AddDbContextFactory provides a per-call
        // DbContext (UoW lifetime = method scope), matching the existing
        // "open connection per method" pattern in SqliteMemoryService.
        // EfReflectionGistService uses two repositories (Memory + MemoryLink) in
        // one DbContext for atomic gist+compress+link-creation, replacing the
        // broken legacy split path. See
        // docs/spec/ANI-Data-Layer-UoW-Repository-Refactor-Plan.md Phase 3.
        services.AddDbContextFactory<AniDbContext>(opts =>
        {
            var dbPath = config["Ani:MemoryDbPath"] ?? "ani-memory.db";
            opts.UseSqlite($"Data Source={dbPath};Foreign Keys=True");
        });
        services.AddSingleton<IReflectionGistService, EfReflectionGistService>();

        // Vibe Loop V1 (Apr 29, 2026) — closed-thread structured-record store.
        // See docs/spec/ANI-VibeLoop-V1-Closed-Thread-Producer-Migration-Plan.md.
        services.AddSingleton<IClosedConversationStore, SqliteClosedConversationStore>();
        // Vibe Loop V1.2 — produces ClosedConversationRecord from a closed thread.
        // V1.3 wires this into SqliteConversationService.CloseThreadAsync.
        services.AddSingleton<IClosedConversationSummarizer, ClosedConversationSummarizer>();
        // Vibe Loop V1.5 (May 2, 2026) — retrieval-time biasing service.
        // V1.5a wires this in observational-only via OutreachPhase /
        // ConversationReplyPhase telemetry. V1.5b activates prompt
        // consumption gated on the V1.5a observation window.
        // See docs/spec/ANI-VibeLoop-V1.5-Retrieval-Time-Biasing-Plan.md.
        services.AddSingleton<IVibeBiasService, VibeBiasService>();
        // Issue #46 (2026-05-21) — V1.5a structured-record persistence so
        // Gate 3 + #43 + #45 read recommended-register data from SQLite
        // instead of grepping Serilog output. Same data plane as
        // SqliteClosedConversationStore; new dedicated table
        // vibe_bias_observations.
        services.AddSingleton<IVibeBiasObservationStore, SqliteVibeBiasObservationStore>();
        // Issue #41 Path B (Theme I prerequisite, 2026-05-21) — per-turn
        // expression classification persistence. Replaces the pre-#41 JSON
        // file substrate in Research.razor. Runtime + batch sources share
        // one table; aggregation queries (Cramér's V, cross-tab) read from
        // here for both dashboard heatmap and paper-figure tooling.
        services.AddSingleton<IExpressionClassificationStore, SqliteExpressionClassificationStore>();

        // Theme M Phase M.1 (May 6, 2026) — conscious-substrate gist composer
        // producing tension-state (§4.8) + register-state (§4.3) slices.
        // M.0 telemetry harness + spec tests in place; M.1 ships real slice
        // content. The IRecentGateTripTracker singleton feeds the §4.8 tension-
        // state slice with recent gate-trip awareness.
        // See docs/spec/ANI-Theme-M-Conscious-Substrate-Individuation-Plan.md.
        services.AddSingleton<IRecentGateTripTracker, InMemoryGateTripTracker>();
        services.AddSingleton<IConsciousSubstrateGist, ConsciousSubstrateGistComposer>();

        // Theme M follow-on (2026-05-14) — IEpistemicSubstrateRenderer renders
        // substrate slices with explicit epistemic framing (Mark-asserted vs.
        // Ani-prior vs. self-world). Sibling to IConsciousSubstrateGist:
        // orthogonal concerns (this is *how to treat substrate*; that is *what
        // Ani is feeling*). Consumed by OutreachPhase (and other prompt-building
        // producers in follow-on commits) when AniOptions.EpistemicFramingEnabled
        // is true. Stateless; singleton.
        services.AddSingleton<IEpistemicSubstrateRenderer, EpistemicSubstrateRenderer>();

        // H phase (2026-06-12 / H.9 expansion 2026-06-14) — tri-state routing
        // classifier for the dual+ composition architecture. Pre-composition
        // classifier that routes each turn to Normal / SafePath / VirtualIntimacy
        // composer. Empirical anchors:
        //   - 2026-06-11 puzzle-turn → SafePath class
        //   - 2026-06-14 22:16 "drop the Books and come over here and give me
        //     a kiss" SafeAck → VirtualIntimacy class
        // Uses the configured ChatModel (qwen3:14b in production) for a single
        // tri-state judgment per turn. See IRoutingClassifier and
        // OllamaRoutingClassifier for design rationale.
        services.AddSingleton<IRoutingClassifier, OllamaRoutingClassifier>();

        // Issue #93 Phase 2 (2026-07-06) — LLM-classified tag intent replaces
        // the regex sniff at TagCommand.cs:109. Handles both directions of the
        // substrate-correction loop (confirm / invalidate) with confidence-
        // gated substrate mutation. See ITagIntentClassifier and
        // OllamaTagIntentClassifier for design rationale.
        services.AddSingleton<ITagIntentClassifier, OllamaTagIntentClassifier>();

        // Issue #93 Phase 3 (2026-07-06) — Ani's self-audit classifier. Reads
        // one Interior record's content against confirmed substrate and
        // returns contradicts / grounded / neutral / unknown. Consumed by:
        // (a) the future SubstrateConsistencyInvariant on CognitiveOutputGate
        // for InnerThought/Reflection/WorldExperience artifacts, and (b) the
        // AniRuntime.Eval --classify-contradiction sweep for retroactive
        // invalidation of the 20,626 pre-existing Interior records.
        services.AddSingleton<IContentContradictionClassifier, OllamaContentContradictionClassifier>();

        // Issue #96 (2026-07-15) — Agentic tool-calling classifier. Reads
        // user message + available tool descriptors, returns structured
        // "call this tool with these args" or "no tool" verdict. Runs on
        // the local verifier model (qwen3:14b default) — same seam as
        // ITagIntentClassifier / IContentContradictionClassifier. Not yet
        // consumed by ConversationReplyPipeline; wired here so
        // AniRuntime.Eval --tool-call can resolve it for the empirical
        // baseline harness per Issue #96 test-first discipline.
        services.AddSingleton<IToolCallClassifier, OllamaToolCallClassifier>();

        // Issue #96 (2026-07-15) — Encapsulates classify → dispatch. Enumerates
        // all registered IToolCallableAction at construction and exposes a
        // one-liner TryInvokeAsync to the ConversationReplyPipeline. Not
        // consumed at runtime unless AniOptions.ToolCallingEnabled = true.
        services.AddSingleton<IToolCallInvocation, ToolCallInvocation>();

        // Theme O Phase O.2 (May 10, 2026) — Theme J invariants migrated onto
        // the cognitive pipeline as Post-stage handlers via
        // InvariantToHandlerAdapter (registered through .UsePostInvariant<T>()
        // in the fluent builder). The legacy CognitiveOutputGate stays in
        // place but is now a pass-through to CognitivePipeline.RunPostOnlyAsync
        // (see CognitiveOutputGate.cs Theme O.2 header). Producers' call sites
        // do not change.
        //
        // Registration order = execution order (§9 lock 3, May 10 18:06 CDT).
        // Convention preserved from Theme J: structural checks first, content
        // checks next, semantic checks last. The whole pipeline shape is
        // grep-able in one place; reorders are a one-line edit at this call
        // site, not a hunt across DI registrations.
        //
        // Theme N N.5 FrameCoherenceChecker is NOT migrated in O.2 — it stays
        // wired directly into OutreachPhase (its O.3 scope migrates it onto
        // the pipeline as a Post-stage handler reading ctx.Frame). See
        // docs/spec/ANI-Theme-O-Cognitive-Pipeline-Middleware-Plan.md §6 O.3.
        services.AddCognitivePipeline(p => p
            // ── Content checks (anti-parrot first; lowest-cost) ───────────────
            .UsePostInvariant<AntiParrotInvariant>()
            // Theme J Phase J.5h-prelude (May 3, 2026) — universal self-echo.
            // Lifts the prior per-producer ParrotingDetector check onto the
            // gate so every artifact with PriorAniMessages context routes
            // through the same check, including J.5a remediation regen output.
            .UsePostInvariant<SelfEchoInvariant>()
            // Theme J Phase J.5h (Issue #47, 2026-05-21) — class-wide source
            // attribution. Empirical anchor: 26 of 26 one-sided ClosedThreadSummary
            // closures over May 14 → May 21 fabricated Mark-side narration.
            // Applies to ClosedThreadSummary + InnerThought producer kinds when
            // ContactRecentMessages is non-null and empty (signal: source had
            // zero contact turns). See SourceAttributionInvariant.cs.
            .UsePostInvariant<SourceAttributionInvariant>()
            .UsePostInvariant<PromptTemplateLeakInvariant>()
            // Theme J Phase J.5b (Apr 30, 2026) — confabulation invariant via LMKit.
            .UsePostInvariant<ConfabulationInvariant>()
            // Theme J Phase J.5g (May 2, 2026) — Door C universalised from
            // OutreachPhase.EvaluateCoherenceAsync. Catches contact-facing
            // outputs that only make sense if the reader had access to the
            // writer's inner thoughts. See InnerThoughtBleedInvariant.cs.
            .UsePostInvariant<InnerThoughtBleedInvariant>()
            // Door B Truth-Verification Sub-claim 2 (May 2, 2026) — day-of-week.
            // See TemporalAnchorInvariant.cs and ANI-Coherence-Gate-Door-B-Design.md.
            .UsePostInvariant<TemporalAnchorInvariant>()
            // Door B Truth-Verification Sub-claim 3 (May 2, 2026) — state-now
            // past-tense time-of-day claims. See StateNowInvariant.cs.
            .UsePostInvariant<StateNowInvariant>()
            // Door B Truth-Verification Sub-claim 1 (May 2, 2026) — temporal-anchor
            // substrate verification. See TemporalSubstrateInvariant.cs.
            .UsePostInvariant<TemporalSubstrateInvariant>()
            // Door B Truth-Verification Sub-claim 4 (May 3, 2026) — type-aware
            // addressee-name verification ("hey perez…" canonical case).
            .UsePostInvariant<AddresseeNameInvariant>()
            // Door B Truth-Verification Sub-claim 5 (May 3, 2026) — present-tense
            // time-of-day verification ("good morning"/"good night"/"it's late").
            .UsePostInvariant<SubstrateTimeOfDayInvariant>()
            // Theme M / coreference (May 6, 2026) — direct-address invariant.
            // RETIRED 2026-07-10. Rationale: empirical run of two consecutive
            // dashboard SafeAcks on the same day (10:26 WILSON + 11:25
            // Hallmark rom-com), both driven by direct-address firing on
            // legitimate scene-within-scene narrative device — Ani authoring
            // dialogue where Mark appears as a character in the scene she's
            // showing to him ("hallmark: ani, darling..."  "me: yes. i'm
            // already writing it — every time HE texts first..."). The rule
            // ("no he/him/his in output when Mark is addressee") lacks the
            // linguistic context to distinguish this from actual absent
            // third-person reference. Mark, 2026-07-10 12:10 CDT: "do we
            // really need this gate anymore? it's been legitimately a
            // problem and very rarely catches something that is really a
            // problem." Class it protected against (Ani genuinely forgetting
            // Mark is the addressee) has never surfaced as an observed
            // failure mode; the false-positive cost dominates.
            //
            // The DirectAddressRewriter (producer-side, K.4c-scoped to
            // contact-name proper-noun swap only) stays live — that catches
            // a different, narrower class ("mark's desk" → "your desk")
            // that IS a real failure mode. Only the invariant is retired.
            //
            // .UsePostInvariant<DirectAddressInvariant>()
            // FC-002 local defense (2026-05-14) — ThreeAxisClaimInvariant.
            // Defense-in-depth against Shared/Mark-world factual-novel claims
            // (windshield / kitchen-lights / hoodie class) that escape the
            // composition framing slices. Self-gates via
            // AniOptions.LocalThreeAxisInvariantEnabled (default off) — flip
            // after substrate-aware v1 lands to avoid false-positives on
            // legitimate substrate-supported claims.
            .UsePostInvariant<ThreeAxisClaimInvariant>()
            // Theme P Phase P.1 (May 11, 2026) — cross-class cloud verifier as
            // ADDITIONAL post-stage handler (plan-doc §9.1 additive framing).
            // All local invariants above continue to fire unchanged; the cloud
            // handler runs on top as defense-in-depth. Position in the chain
            // does not affect correctness — registration order only determines
            // which handler short-circuits first on a multi-violation case.
            // Self-gates via AniOptions.FrontierVerifierEnabled; never reaches
            // into other handlers' applicability.
            .UsePostHandler<FrontierVerifierHandler>()
        );
        services.AddSingleton<ICognitiveOutputGate, CognitiveOutputGate>();

        // Frontier verifier — Theme P P.1 introduced AnthropicVerifierClient
        // (cloud Sonnet). Gate-stack reduction Step 3 (2026-05-15) adds
        // OllamaVerifierClient (local Qwen 14B). AniOptions.FrontierVerifierProvider
        // selects which gets registered against IFrontierVerifierClient. Both
        // use typed HttpClient for properly-disposed clients.
        var verifierProvider = config
            .GetSection("Ani").Get<AniRuntime.Core.AniOptions>()?.FrontierVerifierProvider
            ?? FrontierVerifierProviderKind.Local;
        if (verifierProvider == FrontierVerifierProviderKind.Anthropic)
        {
            services.AddHttpClient<IFrontierVerifierClient, AnthropicVerifierClient>();
        }
        else
        {
            services.AddHttpClient<IFrontierVerifierClient, OllamaVerifierClient>();
        }

        // Theme N Phase N.3 (May 8, 2026) — outreach source-frame selector.
        // Wired into OutreachPhase via constructor injection; gated at runtime
        // by AniOptions.OutreachFrameSelectorEnabled (default off — registration
        // is safe because the selector is null-safe and the consumer guards on
        // the flag before calling it). Flag flip is N.4 scope after canary
        // observation. See ANI-Theme-N-Outreach-Grounding-Source-Typing-Plan.md
        // §10 N.3 for the rollout sequence.
        services.AddSingleton<IOutreachFrameSelector, AniRuntime.Loops.Coreference.OutreachFrameSelector>();

        // Theme N Phase N.5 (May 10, 2026) — post-composition frame-coherence
        // checker. Architectural complement to IOutreachFrameSelector: the
        // selector picks the frame BEFORE composition; the checker enforces
        // the composition MATCHES the frame. Reusable across producer paths
        // (OutreachPhase + future N.6 reactive-share + future N.7
        // ConversationReplyPhase). Phase 1 wired into OutreachPhase only;
        // gated by the same OutreachFrameSelectorEnabled flag as N.3. See
        // ANI-Theme-N-Outreach-Grounding-Source-Typing-Plan.md §10 N.5.
        services.AddSingleton<IFrameCoherenceChecker, AniRuntime.Loops.Coreference.FrameCoherenceChecker>();

        services.AddSingleton<DesireEngine>();

        // ── LLM ───────────────────────────────────────────────────────────────────
        services.AddHttpClient<IOllamaClient, OllamaClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OllamaOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout     = TimeSpan.FromMinutes(2);
        });

        // Contribution 9 PR-2 (Issue #68): EmoLLaMA-chat-7B substrate scorer.
        // Single source of truth for continuous-vector emotion measurement,
        // replacing the two divergent Ollama-via-prompt classifier paths from #66.
        services.AddSingleton<IEmotionalSubstrateScorer, EmoLLamaSubstrateScorer>();

        // ── Voice & Media (Feature 20) — conditional on Voice:Enabled ─────────────
        var voiceEnabled = config.GetValue<bool>("Voice:Enabled");
        services.AddSingleton<MediaCacheService>();

        if (voiceEnabled)
        {
            // Batch voice (Twilio Record webhooks)
            services.AddHttpClient<ITextToSpeechService, ElevenLabsTextToSpeechService>();
            services.AddHttpClient<ISpeechToTextService, WhisperSpeechToTextService>();
            services.AddSingleton<TwilioVoiceHandler>();
            services.AddSingleton<VoiceMediaEnrichmentService>();

            // Voice orchestrator decomposition (SOLID §5.2) — session map is the
            // only stateful piece, so it's the only Singleton; everything else
            // is stateless and Transient so consumers stay free of captives.
            services.AddSingleton<AniRuntime.Voice.Abstractions.IVoiceSessionStore,
                AniRuntime.Voice.Internal.VoiceSessionStore>();
            services.AddTransient<AniRuntime.Voice.Abstractions.IVoiceContextBuilder,
                AniRuntime.Voice.Internal.VoiceContextBuilder>();
            services.AddTransient<AniRuntime.Voice.Abstractions.IVoiceReplyGenerator,
                AniRuntime.Voice.Internal.VoiceReplyGenerator>();
            services.AddTransient<AniRuntime.Voice.Abstractions.IVoiceAudioSynthesizer,
                AniRuntime.Voice.Internal.VoiceAudioSynthesizer>();
            services.AddTransient<AniRuntime.Voice.Abstractions.IVoiceTwimlBuilder,
                AniRuntime.Voice.Internal.VoiceTwimlBuilder>();
            services.AddTransient<VoiceConversationService>();

            // Streaming voice (MAUI app WebSocket — Phase 5)
            var streamingEnabled = config.GetValue<bool>("Voice:StreamingEnabled");
            if (streamingEnabled)
            {
                services.AddTransient<IStreamingSpeechToTextService, DeepgramStreamingSTTService>();
                // v3 HTTP streaming replaces v2 WebSocket — supports audio tags
                services.AddTransient<IStreamingTextToSpeechService, ElevenLabsV3StreamingService>();
                services.AddSingleton<VoiceTurnPipeline>();
                services.AddSingleton<StreamingVoiceOrchestrator>();
            }
        }

        // ── Image sharing (Phase 5a) — conditional on Images:Enabled ─────────────
        var imagesEnabled = config.GetValue<bool>("Images:Enabled");
        if (imagesEnabled)
        {
            services.AddSingleton<IImageSelectionService, ImageSelectionService>();
            services.AddSingleton<ImageMediaEnrichmentService>();
        }

        // ── Composite media enrichment — assembles voice + image enrichments ──────
        services.AddSingleton<IMediaEnrichmentService>(sp =>
        {
            var enrichments = new List<IMediaEnrichmentService>();
            var voice = sp.GetService<VoiceMediaEnrichmentService>();
            if (voice is not null) enrichments.Add(voice);
            var image = sp.GetService<ImageMediaEnrichmentService>();
            if (image is not null) enrichments.Add(image);
            return new CompositeMediaEnrichmentService(enrichments);
        });

        // ── Actions ───────────────────────────────────────────────────────────────
        services.AddSingleton<AniActionDispatcher>();
        services.AddSingleton<IAniAction, TwilioSmsAction>();
        services.AddSingleton<IAniAction, MemoryWriteAction>();

        // Issue #96 (2026-07-15) — Tool-callable actions (LLM-invokable
        // skills). Registered as IToolCallableAction so a future turn-level
        // loop in ConversationReplyPipeline can enumerate them, hand the
        // classifier their descriptors, and dispatch by name. Not consumed
        // by the runtime pipeline yet — gated on Issue #96's remaining
        // acceptance criteria (feature flag, live observation window).
        services.AddSingleton<IToolCallableAction, RecallMemoryAction>();

        // ── Perception sources ────────────────────────────────────────────────────
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPerceptionSource, TimePerceptionSource>();
        services.AddSingleton<IPerceptionSource, TemporalGapPerceptionSource>();
        services.AddHttpClient("rss");
        services.AddSingleton<IPerceptionSource, RssPerceptionSource>();
        services.AddSingleton<IPerceptionSource, ContactStatePerceptionSource>();
        services.AddHttpClient("weather");
        services.AddSingleton<IPerceptionSource, WeatherPerceptionSource>();
        services.AddHttpClient("twilio");
        services.AddHttpClient("elevenlabs-v3");
        services.AddSingleton<TwilioInboundPerceptionSource>();
        services.AddSingleton<IPerceptionSource>(sp => sp.GetRequiredService<TwilioInboundPerceptionSource>());
        services.AddSingleton<IChatInbound>(sp => sp.GetRequiredService<TwilioInboundPerceptionSource>());

        // Agentic Lens Layer 1 Phase 1d: rolling-window tracker for retrieval origin
        // distributions + self-dominance perception source. The tracker is always
        // registered (writer lifetime tied to ContextBuilder); the perception source
        // is gated at runtime by AniOptions.RetrievalDominancePerceptionEnabled.
        services.AddSingleton<IRetrievalOriginTracker, RetrievalOriginTracker>();
        services.AddSingleton<IPerceptionSource, RetrievalSelfDominancePerceptionSource>();

        // Theme R.1 (#64, 2026-05-24) — most-recent inner-thought dominant register,
        // populated into ContextSnapshot.DominantRegister for composer-side branching.
        services.AddSingleton<IDominantRegisterTracker, DominantRegisterTracker>();

        // Theme R.4 (#64, 2026-05-24) — most-recent Layer 2 motivation vector,
        // populated into ContextSnapshot.MotivationVector for composer-side branching.
        services.AddSingleton<IMotivationVectorTracker, MotivationVectorTracker>();

        // Internal-State Perception Framework — signal #1 (Apr 27, 2026): register
        // saturation. Polls active emotional contributions; emits an interior
        // perception when one register dominates above threshold. Default off via
        // AniOptions.RegisterSaturationPerceptionEnabled.
        services.AddSingleton<IPerceptionSource, RegisterSaturationPerceptionSource>();

        // Outage Perception Source (Apr 27, 2026; backlog 15.19, designed Apr 15
        // from the Apr 14-15 power outage). Watches the per-source health tracker
        // populated by PerceptionPhase; emits an interior perception when ≥3
        // sources have been failing continuously for ≥15 minutes, and a recovery
        // perception when health returns. Default off via
        // AniOptions.OutagePerceptionEnabled. The tracker itself is registered
        // as a singleton so PerceptionPhase (writer) and OutagePerceptionSource
        // (reader) share the same in-memory state.
        services.AddSingleton<IPerceptionSourceHealthTracker, PerceptionSourceHealthTracker>();
        services.AddSingleton<IPerceptionSource, OutagePerceptionSource>();

        // services.AddSingleton<IPerceptionSource, HomeAssistantSource>();
        // services.AddSingleton<IPerceptionSource, CalendarPerceptionSource>();

        // ── Emergence layer (E1 — passive observation) ────────────────────────────
        var emergenceEnabled = config.GetValue<bool>("Emergence:Enabled");
        services.AddEmergence(emergenceEnabled);

        // ── Dashboard (Blazor Server + REST API) ──────────────────────────────────
        services.AddDashboard();

        // ── Reply channels (SRP: reply generation ≠ delivery) ──────────────────────
        services.AddSingleton<IReplyChannel, AniRuntime.Actions.SmsReplyChannel>();
        services.AddSingleton<IReplyChannel, AniRuntime.Dashboard.DashboardReplyChannel>();
        services.AddSingleton<IReplyChannelResolver, ReplyChannelResolver>();

        // ── World Layer — experiential grounding for inner life ─────────────────
        services.Configure<WorldSeedOptions>(config.GetSection("WorldSeed"));
        services.AddSingleton<WorldSeedService>();

        // ── Cognitive cycle ───────────────────────────────────────────────────────
        // AdminCommandHandler refactor (§5.1, 2026-05-18): the previous god-object
        // is now a thin dispatcher over IAdminCommand-implementing classes. Each
        // command is its own type, registered Transient. The dispatcher itself
        // and the test-mode tracker are also Transient/Singleton per the lifetime
        // discipline noted in ANI-Orchestrator-SOLID-Refactor-Plan.md §7.
        services.AddSingleton<ITestModeTracker, AniRuntime.Loops.Admin.TestModeTracker>();
        // Phase 5 DI audit (May 18 2026): admin commands + dispatcher were
        // initially Transient. Consumer chain: TwilioInboundPerceptionSource
        // (Singleton) → IAdminCommandHandler — captures the Transient at first
        // resolution, defeating the Transient lifetime. Commands are all
        // stateless; making them Singleton is correct and explicit.
        services.AddSingleton<IAdminCommand, AniRuntime.Loops.Admin.Commands.HelpCommand>();
        services.AddSingleton<IAdminCommand, AniRuntime.Loops.Admin.Commands.TestModeOnCommand>();
        services.AddSingleton<IAdminCommand, AniRuntime.Loops.Admin.Commands.TestModeOffCommand>();
        services.AddSingleton<IAdminCommand, AniRuntime.Loops.Admin.Commands.StatusCommand>();
        services.AddSingleton<IAdminCommand, AniRuntime.Loops.Admin.Commands.ResetMoodCommand>();
        services.AddSingleton<IAdminCommand, AniRuntime.Loops.Admin.Commands.NewThreadCommand>();
        services.AddSingleton<IAdminCommand, AniRuntime.Loops.Admin.Commands.RebuildLinksCommand>();
        services.AddSingleton<IAdminCommand, AniRuntime.Loops.Admin.Commands.RebuildEmergenceCommand>();
        services.AddSingleton<IAdminCommand, AniRuntime.Loops.Admin.Commands.DiagnoseCommand>();
        services.AddSingleton<IAdminCommand, AniRuntime.Loops.Admin.Commands.TagCommand>();
        services.AddSingleton<IAdminCommand, AniRuntime.Loops.Admin.Commands.AuditCommand>();
        // Stateless dispatcher; consumed by the Singleton TwilioInboundPerceptionSource
        // via IAdminCommandHandler, so Singleton is required (not just safe).
        services.AddSingleton<AdminCommandHandler>();
        services.AddSingleton<IAdminCommandHandler>(sp => sp.GetRequiredService<AdminCommandHandler>());
        services.AddSingleton<EmotionalProcessor>();
        // 2026-05-19 — sub-builders of the ContextBuilder SRP decomposition.
        // See `ANI-Testability-Architecture-Plan.md` §2. Singleton matches
        // ContextBuilder's lifetime (consumes them via DI).
        services.AddSingleton<IEmotionalContextBuilder,
            AniRuntime.Loops.Context.EmotionalContextBuilder>();
        services.AddSingleton<IRetrievalContextBuilder,
            AniRuntime.Loops.Context.RetrievalContextBuilder>();
        services.AddSingleton<IConversationContextBuilder,
            AniRuntime.Loops.Context.ConversationContextBuilder>();
        services.AddSingleton<IEpistemicContextBuilder,
            AniRuntime.Loops.Context.EpistemicContextBuilder>();
        services.AddSingleton<IStateContextBuilder,
            AniRuntime.Loops.Context.StateContextBuilder>();
        services.AddSingleton<ContextBuilder>();
        services.AddSingleton<AniRuntime.LLM.ContextCompressor>();
        services.AddSingleton<AniRuntime.LLM.KeywordExtractor>();
        services.AddSingleton<IIntentExtractor, AniRuntime.LLM.IntentExtractor>();
        services.AddSingleton<IConversationGateState, ConversationGateState>();
        services.AddSingleton<PerceptionPhase>();
        services.AddSingleton<InnerThoughtPhase>();
        services.AddSingleton<ClaimVerificationPhase>();
        // §5.4 — extracted from ConversationReplyPhase instance state. Singleton
        // because the withdrawal window is process-wide and consumed by both
        // the reply phase and the cognitive cycle's outreach gate.
        services.AddSingleton<IWithdrawalStateTracker, WithdrawalStateTracker>();
        // §5.4c — post-reply emotional processing (shift + care + anchors +
        // hurt + withdrawal trigger) extracted so the reply pipeline drops
        // EmotionalProcessor as a direct dep. Stateless and consumed by the
        // Singleton ConversationReplyPipeline → Singleton matches.
        services.AddSingleton<IPostReplyEmotionalProcessor, PostReplyEmotionalProcessor>();
        // §5.4d — dispatch + persist + desire reset extracted. Removes three
        // pipeline deps (IReplyChannelResolver, DesireEngine, and the dead
        // AniActionDispatcher injection). Stateless → Singleton.
        services.AddSingleton<IReplyDispatcher, ReplyDispatcher>();
        // §5.4e — output-gate evaluation + remediation cycle extracted.
        // Drops ICognitiveOutputGate + IRecentGateTripTracker from the pipeline.
        // Stateless → Singleton.
        services.AddSingleton<IReplyEvaluator, ReplyEvaluator>();
        // §5.4b — reply body lives in the pipeline; phase is a thin facade.
        // Phase 5 DI audit (May 18 2026): both registrations were initially
        // Transient on the reasoning "consumed only by the Transient phase,
        // so no captive." That was wrong — the phase is also consumed by the
        // Singleton CognitiveCyclePipeline, which captured Transient instances
        // at first resolution anyway. Pipeline + phase are stateless and the
        // consumer chain is Singleton; Singleton is correct and explicit.
        services.AddSingleton<IConversationReplyPipeline, ConversationReplyPipeline>();
        services.AddSingleton<ConversationReplyPhase>();
        // Outreach decomposition (SOLID §5.3) — phase is a thin router.
        // Phase 5 DI audit (May 18 2026): OutreachPipeline + OutreachPhase
        // were initially Transient on the reasoning "consumed only by the
        // Transient phase, so no captive." That was wrong — the phase is
        // also consumed by the Singleton CognitiveCyclePipeline, which
        // captured those Transients at first resolution. Pipeline + phase are
        // stateless; Singleton matches the consumer chain. ThreadRecorder
        // remains Singleton (stateless but shared with Singleton ReactiveShareService).
        // ReactiveShareService + SilenceChoiceRecorder hold per-day / cooldown
        // state and are Singleton by intent.
        services.AddSingleton<IOutboundThreadRecorder, AniRuntime.Loops.Outreach.OutboundThreadRecorder>();
        services.AddSingleton<IOutreachPipeline, AniRuntime.Loops.Outreach.OutreachPipeline>();
        services.AddSingleton<IReactiveShareService, AniRuntime.Loops.Outreach.ReactiveShareService>();
        services.AddSingleton<ISilenceChoiceRecorder, AniRuntime.Loops.Outreach.SilenceChoiceRecorder>();
        services.AddSingleton<OutreachPhase>();
        services.AddSingleton<ReflectionPhase>();
        // §5.5 — cycle body lives in CognitiveCyclePipeline. SINGLETON because
        // it holds the per-process cycle-count + lastAssociativeAnchor state
        // that connects one cycle to the next (creative drift between cycles).
        // Processor is the public entry point + static legacy facades —
        // stateless but Singleton-paired so the consuming AniHeartbeatService
        // (also Singleton) doesn't create a captive-dependency situation.
        services.AddSingleton<ICognitiveCyclePipeline, CognitiveCyclePipeline>();
        services.AddSingleton<CognitiveCycleProcessor>();
        // S6: SessionNotifier is a lightweight singleton with no dependencies — breaks the
        // circular DI chain. AniHeartbeatService wires itself as the handler in its constructor.
        services.AddSingleton<SessionNotifier>();
        services.AddSingleton<ISessionNotifier>(sp => sp.GetRequiredService<SessionNotifier>());
        services.AddHostedService<AniHeartbeatService>();

        // ── Feature 41: Diagnostic service — automated log scanning ──────────────
        services.AddSingleton<IDiagnosticService, DiagnosticService>();
        services.AddHostedService<DiagnosticScheduler>();

        // ── LearnedGeek.ML — LM-Kit emotion/sarcasm classification ─────────────
        services.Configure<MLOptions>(config.GetSection("LMKit"));
        services.AddLearnedGeekML();
        // 2026-05-20 — types moved out of LearnedGeek.ML library back to ANI;
        // AddLearnedGeekML no longer auto-registers them. Register here directly.
        // See learnedgeek-libs Issues #1 (PersonaSummaryCache) and #2 (tag-mapping
        // subsystem + ClassificationComparisonService) for the rationale.
        services.AddSingleton<PersonaSummaryCache>();
        services.AddSingleton<
            AniRuntime.Voice.TagMapping.ITagMappingService,
            AniRuntime.Voice.TagMapping.TagMappingService>();
        services.AddSingleton<AniRuntime.Voice.TagMapping.MLVoiceTagEnricher>();
        services.AddSingleton<AniRuntime.Dashboard.Classification.ClassificationComparisonService>();
    }
}
