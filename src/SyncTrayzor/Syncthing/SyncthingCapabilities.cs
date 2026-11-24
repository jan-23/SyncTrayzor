using System;

namespace SyncTrayzor.Syncthing
{
    public interface ISyncthingCapabilities
    {
        bool SupportsDebugFacilities { get; }
        bool SupportsDevicePauseResume { get; }

        /// <summary>
        /// Syncthing reports which other devices have a valid copy of a file, so a subfolder's local copy can be
        /// deleted safely ("Remove from this device")
        /// </summary>
        bool SupportsReliableAvailability { get; }
    }

    public class SyncthingCapabilities : ISyncthingCapabilities
    {
        private static readonly Version debugFacilitiesIntroduced = new(0, 12, 0);
        private static readonly Version devicePauseResumeIntroduced = new(0, 12, 0);

        // Up to v2.1.7, /rest/db/file's "availability" also lists devices whose copy is invalid (e.g. ignored there,
        // and possibly deleted). Fixed by syncthing/syncthing#10944, which is on main but in no release yet. Releases
        // are cut from main, so the next one after v2.1.7 should contain it: check when it's out.
        private static readonly Version reliableAvailabilityIntroduced = new(2, 1, 8);

        public Version SyncthingVersion { get; set; } = new(0, 0, 0);

        public bool SupportsDebugFacilities => SyncthingVersion >= debugFacilitiesIntroduced;
        public bool SupportsDevicePauseResume => SyncthingVersion >= devicePauseResumeIntroduced;
        public bool SupportsReliableAvailability => SyncthingVersion >= reliableAvailabilityIntroduced;
    }
}
