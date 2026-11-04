using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ESPresense.Companion.Tests")]
// Lets the fusion-validation harness (SyntheticFixtureBuilder) drive CsiFusionService.Tick and
// CsiMotionTracker.Observe deterministically, the same way the unit tests do.
[assembly: InternalsVisibleTo("ESPresense.Simulation")]
