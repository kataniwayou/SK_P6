namespace Messaging.Contracts.Projections;

/// <summary>
/// Single source of truth for the L2 (Redis) projection key formats (L2-PROJECT-02 / HARDEN-03).
/// Hoisted into the Messaging.Contracts leaf (Phase 21) so the writer
/// (<c>BaseApi.Service.RedisProjectionKeys</c>) and reader (<c>Orchestrator.OrchestratorL2Keys</c>)
/// consume ONE shape — a future GUID-format/suffix change cannot silently desynchronize them.
/// <para>
/// The scheme is FLAT: a single prefix followed by GUID(s), with NO type
/// discriminator (D-02). The live liveness builders are the per-replica <see cref="PerInstance"/> +
/// <see cref="InstanceIndex"/> pair (the legacy flat <c>Processor(Guid)</c> builder was deleted in
/// Phase 61, D-11 — the per-replica reshape's reader swapped off it). GUIDs render in the default
/// <c>Guid.ToString()</c> ("D") format — hyphenated — NOT the "N" (32-digit) format; <see cref="Root"/>
/// makes this explicit with the <c>:D</c> format specifier (byte-identical to a bare interpolation).
/// The prefix is now a compile-time <c>const Prefix = "skp:"</c> owned HERE (D-01, Phase 22),
/// consumed by both forwarders — it is no longer a per-host config value and no longer a builder
/// parameter. This eliminates any config-injection path into key names.
/// </para>
/// <list type="bullet">
///   <item><description>ParentIndex: <c>{Prefix}</c> (the bare prefix — the parent-index SET key)</description></item>
///   <item><description>Root: <c>{Prefix}{workflowId}</c></description></item>
///   <item><description>Step: <c>{Prefix}{workflowId}:{stepId}</c></description></item>
///   <item><description>PerInstance: <c>{Prefix}proc:{processorId:D}:{instanceId}</c> — KEY-01, the per-replica liveness key.</description></item>
///   <item><description>InstanceIndex: <c>{Prefix}proc:{processorId:D}</c> — KEY-02, the per-processor instance-index SET key (prefix of PerInstance).</description></item>
///   <item><description>ExecutionData: <c>{Prefix}data:{entryId:D}</c> — the sole GUID-keyed data builder (D-08; the legacy 64-hex content-addressed string overload was removed in v4.0.0).</description></item>
///   <item><description>OutputData: <c>{Prefix}out:{messageId:D}</c> — D-10, the per-message output blob key.</description></item>
/// </list>
/// </summary>
public static class L2ProjectionKeys
{
    public const string Prefix = "skp:";

    public static string ParentIndex() => Prefix;

    public static string Root(Guid workflowId) => $"{Prefix}{workflowId:D}";

    public static string Step(Guid workflowId, Guid stepId) => $"{Prefix}{workflowId:D}:{stepId:D}";

    /// <summary>KEY-01: the per-INSTANCE processor-liveness key — <c>skp:proc:{processorId:D}:{instanceId}</c>.
    /// The <c>proc:</c> discriminator marks the per-replica liveness scheme (the legacy flat
    /// <c>Processor(Guid)</c> key was deleted in Phase 61, D-11). <paramref name="instanceId"/> is the
    /// already-resolved pod identity (see <c>Messaging.Contracts.Identity.InstanceId.Resolve</c>) — a
    /// plain string, NOT a Guid.</summary>
    public static string PerInstance(Guid processorId, string instanceId)
        => $"{Prefix}proc:{processorId:D}:{instanceId}";

    /// <summary>KEY-02: the per-processor instance-index SET key — <c>skp:proc:{processorId:D}</c> — that
    /// the Phase-60 writer SADDs each replica's instanceId into and the Phase-61 gate SMEMBERS. It is the
    /// exact prefix (before the trailing <c>:{instanceId}</c>) of <see cref="PerInstance"/>.</summary>
    public static string InstanceIndex(Guid processorId)
        => $"{Prefix}proc:{processorId:D}";

    /// <summary>D-08: the sole GUID-keyed L2 data builder — <c>skp:data:{entryId:D}</c> (no TTL baked
    /// in; caller concern). The legacy 64-hex content-addressed string overload was removed in v4.0.0.</summary>
    public static string ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}";

    /// <summary>D-10: the per-message output blob key — <c>skp:out:{messageId:D}</c> — written by the
    /// Pre inline tail / Post / INJECT (write L2[messageId]=data, completed-only). Distinct `out:`
    /// namespace separates output blobs (by messageId) from input blobs (`skp:data:` by entryId). The
    /// jittered random[ExecutionDataTtl, 2×ExecutionDataTtl] TTL is a caller concern (passed on the write —
    /// see <see cref="OutputDataTtl"/>, the single source of truth for the policy).</summary>
    public static string OutputData(Guid messageId) => $"{Prefix}out:{messageId:D}";

    /// <summary>IN-04 (Phase 70): the SINGLE source of truth for the L2[messageId] output-blob TTL policy —
    /// jittered <c>random[ttlSeconds, 2×ttlSeconds]</c>. BOTH the processor's <c>OutputTail</c> (Pre/Post
    /// inline tail) AND the keeper's <c>InjectConsumer</c> call this so a keeper-completed result and a
    /// directly-completed one cannot desynchronize lifetimes (they previously duplicated the formula across
    /// two assemblies). The <paramref name="ttlSeconds"/> floor still comes from each writer's own option
    /// (ProcessorLivenessOptions / RecoveryOptions — both default 300); only the policy is shared here.</summary>
    public static TimeSpan OutputDataTtl(int ttlSeconds)
        => TimeSpan.FromSeconds(Random.Shared.Next(ttlSeconds, 2 * ttlSeconds + 1));

    /// <summary>D-03: probe scratch key — short-TTL write-then-delete; the TTL is the crash net-zero net.</summary>
    public static string KeeperProbe(string h) => $"{Prefix}keeper:probe:{h}";   // "skp:keeper:probe:{h}"
}
