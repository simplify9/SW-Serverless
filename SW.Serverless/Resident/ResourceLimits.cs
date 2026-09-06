using System;

namespace SW.Serverless.Resident
{
    /// <summary>
    /// The ceilings one adapter process runs under.
    ///
    /// An adapter is a separate process the host does not otherwise constrain, so without these a
    /// single runaway payload is bounded by nothing but the machine — and every other adapter on
    /// the node goes down with it. Zero on any field means "leave the host default alone", which is
    /// how a caller changes one ceiling without having to restate the others.
    /// </summary>
    public class ResourceLimits
    {
        /// <summary>
        /// Soft RSS ceiling in bytes. Crossing it asks the adapter to DRAIN — finish what is in
        /// flight, then exit — so nothing is lost and the supervisor relaunches it. This is the
        /// ceiling that should normally fire.
        /// </summary>
        public long SoftMemoryLimitBytes { get; set; }

        /// <summary>
        /// Hard RSS ceiling in bytes. Crossing it kills the process tree, and it is also handed to
        /// the runtime as DOTNET_GCHeapHardLimit so an allocation past it fails inside the adapter
        /// rather than taking the node with it.
        ///
        /// Because the runtime reads it at launch, changing this one only takes effect on a
        /// restart — see <see cref="LimitUpdate.RestartRequired"/>.
        /// </summary>
        public long HardMemoryLimitBytes { get; set; }

        /// <summary>
        /// Sustained CPU ceiling, as a percentage of the WHOLE machine — the same figure
        /// <see cref="InstanceHealth.CpuPercent"/> reports, which is processor time divided by
        /// wall time divided by <see cref="Environment.ProcessorCount"/>.
        ///
        /// Worth being exact about, because the intuitive reading is wrong in an expensive
        /// direction: one core pegged flat out on a sixteen-core host reads about 6%, so a ceiling
        /// set at "50%, surely that's half a core" would in fact allow eight cores.
        ///
        /// Deliberately sustained rather than instantaneous: an adapter draining a backlog is
        /// SUPPOSED to peg a core, and recycling it for doing its job well would be a bug wearing a
        /// limit's clothes. It trips only after <see cref="CpuLimitSamples"/> consecutive samples
        /// above the line, and then asks for a drain — there is no hard CPU kill, because killing
        /// on CPU punishes throughput rather than protecting the node.
        /// </summary>
        public double CpuPercentLimit { get; set; }

        /// <summary>
        /// Consecutive over-limit samples before the CPU ceiling trips. Zero uses the host default.
        /// Samples are taken on the heartbeat, so this is a multiple of the heartbeat interval.
        /// </summary>
        public int CpuLimitSamples { get; set; }

        public bool IsEmpty =>
            SoftMemoryLimitBytes <= 0 && HardMemoryLimitBytes <= 0 && CpuPercentLimit <= 0;

        public override string ToString() =>
            $"soft={Mb(SoftMemoryLimitBytes)} hard={Mb(HardMemoryLimitBytes)} cpu={(CpuPercentLimit > 0 ? CpuPercentLimit + "%" : "—")}";

        static string Mb(long bytes) => bytes > 0 ? $"{bytes / 1024 / 1024}MB" : "—";
    }

    /// <summary>What changing an adapter's limits actually did.</summary>
    public class LimitUpdate
    {
        /// <summary>False when there is no such instance to change.</summary>
        public bool Applied { get; set; }

        /// <summary>
        /// True when part of the change cannot take effect until the process is relaunched. The
        /// hard memory ceiling is the one that does this: the runtime reads it at launch. The
        /// caller decides whether to restart now or at a quieter moment — which is the whole
        /// reason this is reported rather than acted on.
        /// </summary>
        public bool RestartRequired { get; set; }

        /// <summary>Why a restart is needed, in words an operator can act on.</summary>
        public string Reason { get; set; }

        /// <summary>The ceilings now in force on the supervised spec.</summary>
        public ResourceLimits Limits { get; set; }
    }
}
