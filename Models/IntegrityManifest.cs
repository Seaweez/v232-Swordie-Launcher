using System.Collections.Generic;
using System.Runtime.Serialization;

namespace v232.Launcher.WPF.Models
{
    [DataContract]
    public sealed class IntegrityManifest
    {
        [DataMember(Name = "format", Order = 1)]
        public int Format { get; set; }

        [DataMember(Name = "releaseId", Order = 2)]
        public string ReleaseId { get; set; }

        [DataMember(Name = "release", Order = 3)]
        public string Release { get; set; }

        [DataMember(Name = "version", Order = 4)]
        public string Version { get; set; }

        [DataMember(Name = "algorithm", Order = 5)]
        public string Algorithm { get; set; }

        [DataMember(Name = "files", Order = 6)]
        public List<IntegrityFile> Files { get; set; }
    }

    [DataContract]
    public sealed class IntegrityFile
    {
        [DataMember(Name = "relativePath", Order = 1)]
        public string RelativePath { get; set; }

        [DataMember(Name = "path", Order = 2)]
        public string Path { get; set; }

        [DataMember(Name = "length", Order = 3)]
        public long Length { get; set; }

        [DataMember(Name = "sha256", Order = 4)]
        public string Sha256 { get; set; }
    }
}
