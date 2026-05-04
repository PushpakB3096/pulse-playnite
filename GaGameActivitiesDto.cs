// Lacro59 Game Activity stores per-game JSON under ExtensionsData\{afbb1a0d-04a1-4d0c-9afa-c6e42ca855b4}\GameActivity\{gameGuid}.json
// Root contains an "Items" array; each row includes "DateSession", "ElapsedSeconds", optional metadata (e.g. "SourceID").
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Pulse
{
    internal sealed class GaGameActivitiesFileDto
    {
        [JsonProperty("Items")]
        public List<GaActivityDto> Items { get; set; }

        [JsonProperty("Id")]
        public Guid? Id { get; set; }
    }

    internal sealed class GaActivityDto
    {
        [JsonProperty("DateSession")]
        public DateTime? DateSession { get; set; }

        [JsonProperty("ElapsedSeconds")]
        public ulong? ElapsedSeconds { get; set; }
    }
}
