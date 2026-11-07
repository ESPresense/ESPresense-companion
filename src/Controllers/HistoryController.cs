using ESPresense.Models;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers
{
    [Route("api/history")]
    [ApiController]
    public class HistoryController(DeviceHistoryStore deviceHistory) : ControllerBase
    {
        [HttpGet("{id}")]
        public async Task<DeviceHistoryResponse> Get(string id)
        {
            var history = await deviceHistory.List(id) ?? new List<DeviceHistory>();
            return new DeviceHistoryResponse(history);
        }

        /// <summary>
        /// A device's rows between <paramref name="start"/> and <paramref name="end"/> (ISO 8601, UTC). <paramref name="best"/> returns only
        /// the best scenario's row of each cycle; <paramref name="every"/> (seconds) keeps one row per scenario per that many seconds, so a
        /// day of one-second fixes comes back as a few thousand points.
        /// </summary>
        [HttpGet("{id}/range")]
        public async Task<DeviceHistoryResponse> GetRange(string id, [FromQuery] DateTime start, [FromQuery] DateTime end, [FromQuery] bool best = false, [FromQuery] int every = 0)
        {
            var history = await deviceHistory.List(id, start, end, best) ?? new List<DeviceHistory>();
            return new DeviceHistoryResponse(Thin(history, every));
        }

        /// <summary>
        /// Keeps the last row of each <paramref name="everySeconds"/> bucket, per series: the best rows form one series (whichever scenario
        /// was best), every other scenario its own. <paramref name="rows"/> must be ordered by time, as the store returns them.
        /// </summary>
        public static IList<DeviceHistory> Thin(IList<DeviceHistory> rows, int everySeconds)
        {
            if (everySeconds <= 0 || rows.Count == 0) return rows;
            var bucketTicks = TimeSpan.FromSeconds(everySeconds).Ticks;
            var kept = new List<DeviceHistory>();
            var lastBucket = new Dictionary<(bool Best, string Scenario), (long Bucket, int Index)>();
            foreach (var row in rows)
            {
                var bucket = (row.When ?? default).Ticks / bucketTicks;
                var series = (row.Best, row.Best ? "" : row.Scenario ?? "");
                if (lastBucket.TryGetValue(series, out var last) && last.Bucket == bucket)
                    kept[last.Index] = row;
                else
                {
                    lastBucket[series] = (bucket, kept.Count);
                    kept.Add(row);
                }
            }
            return kept;
        }
    }

    public record DeviceHistoryResponse(IList<DeviceHistory> history);
}
