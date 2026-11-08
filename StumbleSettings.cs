using UnityEngine;

namespace Stumble
{
    /// <summary>
    /// Settings saved by the Modding API to Stumble.GlobalSettings.json in the
    /// game's save folder. Edit that file for values the in-game menu doesn't
    /// offer (animation names, fall frames). Delete it to restore defaults.
    /// </summary>
    public class StumbleSettings
    {
        // The clumsiness levels and the average seconds of running on the
        // ground between trips for each. Retune the numbers here.
        public static readonly string[] ClumsinessLevels = { "Low", "Medium", "High", "Very High" };
        private static readonly float[] ClumsinessSeconds = { 30f, 15f, 8f, 4f };
        private const int DefaultClumsiness = 1; // Medium

        /// <summary>How clumsy the Knight is: Low, Medium, High or Very High.</summary>
        public string KnightClumsiness = ClumsinessLevels[DefaultClumsiness];

        /// <summary>
        /// Minimum seconds on the ground before a left/right press stands the
        /// Knight up, counted from the impact (when the trip thud plays, or
        /// the landing after a tumble).
        /// </summary>
        public float MinLieTime = 0.35f;

        /// <summary>Play the stand-up animation in reverse as a fall before lying down.</summary>
        public bool FallAnimation = true;

        /// <summary>How much faster than the stand-up animation the fall plays.</summary>
        public float FallSpeed = 1.5f;

        /// <summary>
        /// Collapse runs of repeated frames (held poses, like the kneeling
        /// pause) to a single frame in the fall.
        /// </summary>
        public bool FallSkipHolds = true;

        /// <summary>
        /// Frames of the stand-up clip to leave out of the fall, e.g. "6-11" or
        /// "6-11, 14". Numbers match the frame list written to ModLog.txt
        /// (with DetailedLogging on).
        /// </summary>
        public string FallSkipFrames = "3-15";

        /// <summary>
        /// Seconds the Knight keeps sliding forward after tripping, slowing to
        /// a stop. 0 turns the slide off.
        /// </summary>
        public float SlideTime = 0.75f;

        /// <summary>
        /// True: a slide carries the Knight off ledges and he falls in the
        /// lying pose. False: the slide stops at the edge.
        /// </summary>
        public bool TumbleOffEdges = true;

        /// <summary>
        /// Play a soft landing sound when a tumble drops a short way. Long
        /// falls get the game's own heavy thud either way.
        /// </summary>
        public bool LandingSounds = true;

        /// <summary>Play the soft landing thud when the Knight hits the floor on every trip.</summary>
        public bool TripThud = true;

        /// <summary>Spawn the game's landing dust where the Knight hits the floor.</summary>
        public bool ImpactDust = true;

        /// <summary>
        /// Play the trip thud this many animation frames before the fall
        /// animation ends, to line it up with the moment the body visibly hits
        /// the floor. 0 plays it as the lying pose starts.
        /// </summary>
        public int ThudFramesEarly = 3;

        /// <summary>Knight animation played while tripped.</summary>
        public string LyingClip = "Prostrate";

        /// <summary>Knight animation played when standing back up (and reversed for the fall). Blank skips it.</summary>
        public string RiseClip = "Prostrate Rise";

        /// <summary>
        /// Write extra detail to ModLog.txt: the stand-up animation's frame
        /// list, what the Knight lands on, and areas re-entered after a trip.
        /// Warnings and important events are always logged.
        /// </summary>
        public bool DetailedLogging = false;

        /// <summary>Keeps hand-edited values in a safe range.</summary>
        public void Validate()
        {
            KnightClumsiness = ClumsinessLevels[ClumsinessIndex()];
            MinLieTime = Mathf.Clamp(MinLieTime, 0f, 5f);
            FallSpeed = Mathf.Clamp(FallSpeed, 0.25f, 10f);
            SlideTime = Mathf.Clamp(SlideTime, 0f, 2f);
            ThudFramesEarly = Mathf.Clamp(ThudFramesEarly, 0, 30);
            if (FallSkipFrames == null) FallSkipFrames = "";
            if (LyingClip == null) LyingClip = "";
            if (RiseClip == null) RiseClip = "";
        }

        // Methods rather than properties, so they aren't written to the
        // settings file and the numbers stay out of sight.

        /// <summary>
        /// Position of KnightClumsiness in ClumsinessLevels. Ignores case and
        /// spacing; anything unrecognised counts as Medium.
        /// </summary>
        public int ClumsinessIndex()
        {
            string wanted = (KnightClumsiness ?? "").Replace(" ", "").ToLowerInvariant();
            for (int i = 0; i < ClumsinessLevels.Length; i++)
                if (ClumsinessLevels[i].Replace(" ", "").ToLowerInvariant() == wanted) return i;
            return DefaultClumsiness;
        }

        /// <summary>Average seconds of running on the ground between trips at this clumsiness.</summary>
        public float AverageTripSeconds() => ClumsinessSeconds[ClumsinessIndex()];
    }
}
