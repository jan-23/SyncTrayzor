using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace SyncTrayzor.Syncthing.ApiClient
{
    public class Ignores
    {
        /// <summary>
        /// Lines of the folder's top-level .stignore file
        /// </summary>
        [JsonProperty("ignore")]
        public List<string> Lines { get; set; }

        /// <summary>
        /// The patterns Syncthing matches against, after resolving #include and expanding each line
        /// </summary>
        [JsonProperty("expanded", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Expanded { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string Error { get; set; }
    }

    public class FolderNeed
    {
        [JsonProperty("progress")]
        public List<FolderNeedItem> Progress { get; set; }

        [JsonProperty("queued")]
        public List<FolderNeedItem> Queued { get; set; }

        [JsonProperty("rest")]
        public List<FolderNeedItem> Rest { get; set; }
    }

    public class FolderNeedItem
    {
        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class DbFile
    {
        [JsonProperty("local")]
        public DbFileInfo Local { get; set; }

        [JsonProperty("global")]
        public DbFileInfo Global { get; set; }

        /// <summary>
        /// Other devices which are connected right now and have a valid copy of the global (newest) version
        /// </summary>
        [JsonProperty("availability")]
        public List<DbFileAvailability> Availability { get; set; }
    }

    public class DbFileAvailability
    {
        [JsonProperty("id")]
        public string DeviceId { get; set; }

        [JsonProperty("fromTemporary")]
        public bool FromTemporary { get; set; }
    }

    public class DbFileInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("deleted")]
        public bool Deleted { get; set; }

        [JsonProperty("ignored")]
        public bool Ignored { get; set; }

        [JsonProperty("invalid")]
        public bool Invalid { get; set; }

        [JsonProperty("localFlags")]
        public uint LocalFlags { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("modified")]
        public DateTime Modified { get; set; }

        /// <summary>The version vector, e.g. ["ABCDEFG:1700000000"]</summary>
        [JsonProperty("version")]
        public List<string> Version { get; set; }
    }

    public class TreeEntry
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        public bool IsDirectory => Type == "FILE_INFO_TYPE_DIRECTORY" || Type == "DIRECTORY";
    }
}
