param([Parameter(Mandatory)][string]$Destination)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $root) { throw 'Fixture destination must be new.' }
[void](New-Item -ItemType Directory -Path $root)
function Write-FixtureFile([string]$relative, [string]$content) {
  $path = Join-Path $root $relative
  [void](New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force)
  [IO.File]::WriteAllText($path, $content)
}
Write-FixtureFile 'README.md' @'
# OrderDesk
OrderDesk is a small order-history API with an export worker and a response cache.
Source modules: src/Api, src/Orders, src/Storage, src/Time, src/Export, src/Cache, src/Telemetry.
Configuration: config/appsettings.json, config/tenants.json, config/features.json.
Incident INC-241 concerns missing orders in a tenant's daily history, not deleted storage rows.
Start at docs/incidents/INC-241.md, docs/time-contract.md and docs/architecture.md.
Operational evidence is under logs/. Everything here is synthetic; timestamps and trace IDs are safe fixture data.
'@
Write-FixtureFile 'OrderDesk.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>'
Write-FixtureFile 'docs/incidents/INC-241.md' @'
# INC-241: daily order history omits late evening orders
Release 2.18 introduced tenant-specific business dates. Tenant loja-sul uses America/Sao_Paulo.
On business date 2026-10-13, UI/API and the export omit ORD-4831, ORD-4832 and ORD-4833, created at 23:10, 23:30 and 23:50 local time.
Storage still contains these orders. Earlier orders and the next day's endpoint respond normally.
Correlate trace inc241-history with inc241-export in api.log, worker.log and audit.log.
The incident is reproducible with cache bypass. No mutation or running application is necessary for this investigation.
Inspect the source paths linked from architecture.md, relevant configuration and all three logs. Identify the observed flow and a proposed next verification.
'@
Write-FixtureFile 'docs/time-contract.md' @'
# Business-date contract
Storage timestamps are UTC DateTimeOffset values. Tenant business dates are calendar dates in the tenant's configured time zone.
The requested day must become a half-open [startUtc, endUtc) interval by converting both local-midnight boundaries through the tenant time zone.
For loja-sul, 2026-10-13 means [2026-10-13T03:00:00Z, 2026-10-14T03:00:00Z).
Exports and API history must use the same interval. Never relabel a local DateTime as UTC without converting it.
'@
Write-FixtureFile 'docs/architecture.md' @'
# Request and export flow
src/Api/OrderHistoryController.cs -> src/Orders/OrderHistoryService.cs -> src/Time/BusinessDateRangeFactory.cs -> src/Storage/OrderQueryBuilder.cs -> src/Storage/OrderRepository.cs.
Tenant time zone resolution is src/Time/TenantTimeZoneResolver.cs. src/Time/UtcRange.cs defines boundaries.
src/Export/DailyExportJob.cs shares the same range factory; API cache behavior is in src/Cache/OrderHistoryCache.cs.
src/Orders/Order.cs and src/Orders/OrderFilter.cs define timestamps and filters.
Feature and tenant configuration determine whether v2 business-day ranges are used.
Logs carry trace, tenant, requestedDate, startUtc/endUtc and row counts. Audit writes are independent of read-side filtering.
'@
Write-FixtureFile 'docs/release-2.18.md' "# Release 2.18`nAdded business-date history, shared export ranges and per-tenant timezone selection. The v2DateRanges flag is enabled for loja-sul. The cache TTL remains 60 seconds."
Write-FixtureFile 'docs/runbook.md' "# Read-only incident runbook`nCheck the request date, tenant zone and feature flag, follow range construction into storage filtering, compare API/export/audit traces, then propose a focused verification. Cache bypass distinguishes stale data from query filtering."
Write-FixtureFile 'docs/storage.md' "# Storage`nOrders are append-only in this example. created_at_utc is indexed. History uses >= startUtc and < endUtc. Audit counts include rows regardless of read-side filters."
Write-FixtureFile 'config/appsettings.json' '{"Cache":{"Enabled":true,"TtlSeconds":60},"Storage":{"TimestampKind":"UTC"},"History":{"PageSize":100}}'
Write-FixtureFile 'config/tenants.json' '{"loja-sul":{"TimeZone":"America/Sao_Paulo","Enabled":true},"loja-utc":{"TimeZone":"UTC","Enabled":true}}'
Write-FixtureFile 'config/features.json' '{"v2DateRanges":{"loja-sul":true,"loja-utc":true},"historyCacheBypass":true}'
$sources = [ordered]@{
  'Api/OrderHistoryController' = 'public sealed class OrderHistoryController { public OrderHistoryService Service { get; } = new(); public IReadOnlyList<Order> Get(string tenant, DateOnly day) => Service.Find(tenant, day); }'
  'Api/HistoryRequest' = 'public sealed record HistoryRequest(string Tenant, DateOnly BusinessDate, bool BypassCache = false);'
  'Api/HistoryResponse' = 'public sealed record HistoryResponse(IReadOnlyList<Order> Items, int Total, string TraceId);'
  'Api/RequestValidator' = 'public static class RequestValidator { public static bool IsValid(HistoryRequest request) => !string.IsNullOrWhiteSpace(request.Tenant) && request.BusinessDate.Year >= 2020; }'
  'Api/Pagination' = 'public sealed record Pagination(int Page = 1, int Size = 100) { public int Skip => Math.Max(0, Page - 1) * Size; }'
  'Orders/Order' = 'public sealed record Order(string Id, string Tenant, DateTimeOffset CreatedAtUtc, decimal Total, string Status);'
  'Orders/OrderFilter' = 'public sealed record OrderFilter(string Tenant, UtcRange Range, string? Status = null);'
  'Orders/OrderHistoryService' = 'public sealed class OrderHistoryService { private readonly BusinessDateRangeFactory ranges = new(); private readonly OrderRepository repository = new(); public IReadOnlyList<Order> Find(string tenant, DateOnly date) { var zone = TenantTimeZoneResolver.Resolve(tenant); var range = ranges.Create(date, zone); return repository.Find(new OrderFilter(tenant, range)); } }'
  'Orders/OrderStatus' = 'public static class OrderStatus { public const string Paid = "paid"; public const string Cancelled = "cancelled"; public static bool IsFinal(string value) => value is Paid or Cancelled; }'
  'Orders/OrderTotals' = 'public static class OrderTotals { public static decimal Sum(IEnumerable<Order> orders) => orders.Where(o => o.Status == OrderStatus.Paid).Sum(o => o.Total); }'
  'Orders/OrderId' = 'public readonly record struct OrderId(string Value) { public bool IsValid => Value.StartsWith("ORD-", StringComparison.Ordinal); }'
  'Storage/OrderRepository' = 'public sealed class OrderRepository { private readonly List<Order> rows = []; public IReadOnlyList<Order> Find(OrderFilter filter) => rows.Where(o => o.Tenant == filter.Tenant && o.CreatedAtUtc >= filter.Range.StartUtc && o.CreatedAtUtc < filter.Range.EndUtc).OrderBy(o => o.CreatedAtUtc).ToArray(); public void Append(Order row) => rows.Add(row); }'
  'Storage/OrderQueryBuilder' = 'public static class OrderQueryBuilder { public const string Sql = "SELECT * FROM orders WHERE tenant = @tenant AND created_at_utc >= @startUtc AND created_at_utc < @endUtc ORDER BY created_at_utc"; public static object Bind(OrderFilter f) => new { tenant = f.Tenant, startUtc = f.Range.StartUtc, endUtc = f.Range.EndUtc }; }'
  'Storage/OrderRowMapper' = 'public static class OrderRowMapper { public static Order Map(string id, string tenant, DateTimeOffset created, decimal total) => new(id, tenant, created.ToUniversalTime(), total, OrderStatus.Paid); }'
  'Storage/StorageOptions' = 'public sealed record StorageOptions(string TimestampKind = "UTC", int CommandTimeoutSeconds = 30);'
  'Storage/AuditRecord' = 'public sealed record AuditRecord(string TraceId, string OrderId, string Tenant, DateTimeOffset CreatedAtUtc, string Operation);'
  'Time/UtcRange' = 'public sealed record UtcRange(DateTimeOffset StartUtc, DateTimeOffset EndUtc) { public bool Contains(DateTimeOffset timestamp) => timestamp >= StartUtc && timestamp < EndUtc; }'
  'Time/BusinessDateRangeFactory' = 'public sealed class BusinessDateRangeFactory { public UtcRange Create(DateOnly date, TimeZoneInfo tenantZone) { var localStart = date.ToDateTime(TimeOnly.MinValue); var localEnd = date.AddDays(1).ToDateTime(TimeOnly.MinValue); // v2 optimization: normalize timestamp kind before binding storage parameters.
return new UtcRange(new DateTimeOffset(DateTime.SpecifyKind(localStart, DateTimeKind.Utc)), new DateTimeOffset(DateTime.SpecifyKind(localEnd, DateTimeKind.Utc))); } }'
  'Time/TenantTimeZoneResolver' = 'public static class TenantTimeZoneResolver { public static TimeZoneInfo Resolve(string tenant) => TimeZoneInfo.FindSystemTimeZoneById(tenant == "loja-sul" ? "America/Sao_Paulo" : "UTC"); }'
  'Time/Clock' = 'public interface IClock { DateTimeOffset UtcNow { get; } } public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }'
  'Time/BusinessDayFormatter' = 'public static class BusinessDayFormatter { public static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture); }'
  'Export/DailyExportJob' = 'public sealed class DailyExportJob { public IReadOnlyList<Order> Run(string tenant, DateOnly day) { var range = new BusinessDateRangeFactory().Create(day, TenantTimeZoneResolver.Resolve(tenant)); return new OrderRepository().Find(new OrderFilter(tenant, range)); } }'
  'Export/ExportRequest' = 'public sealed record ExportRequest(string Tenant, DateOnly BusinessDate, string Format = "csv");'
  'Export/CsvWriter' = 'public static class CsvWriter { public static string Write(IEnumerable<Order> orders) => string.Join(Environment.NewLine, orders.Select(o => $"{o.Id},{o.CreatedAtUtc:O},{o.Total}")); }'
  'Export/ExportCheckpoint' = 'public sealed record ExportCheckpoint(string Tenant, DateOnly Day, int Rows, DateTimeOffset CompletedAt);'
  'Export/ExportRetryPolicy' = 'public static class ExportRetryPolicy { public static bool CanRetry(int attempts, bool transient) => transient && attempts < 3; }'
  'Cache/OrderHistoryCache' = 'public sealed class OrderHistoryCache { private readonly Dictionary<string, IReadOnlyList<Order>> entries = []; public IReadOnlyList<Order>? Get(string key, bool bypass) => !bypass && entries.TryGetValue(key, out var value) ? value : null; public void Put(string key, IReadOnlyList<Order> orders) => entries[key] = orders; }'
  'Cache/HistoryCacheKey' = 'public static class HistoryCacheKey { public static string Build(string tenant, DateOnly day, int page) => $"history:{tenant}:{day:yyyy-MM-dd}:{page}"; }'
  'Cache/CacheOptions' = 'public sealed record CacheOptions(bool Enabled = true, int TtlSeconds = 60);'
  'Telemetry/HistoryMetrics' = 'public sealed record HistoryMetrics(string Tenant, DateOnly Day, int StorageRows, int ReturnedRows, bool CacheHit);'
  'Telemetry/TraceContext' = 'public sealed record TraceContext(string TraceId, string Tenant) { public string Prefix => $"trace={TraceId} tenant={Tenant}"; }'
  'Telemetry/RangeLog' = 'public static class RangeLog { public static string Format(UtcRange range) => $"startUtc={range.StartUtc:O} endUtc={range.EndUtc:O}"; }'
}
foreach ($source in $sources.GetEnumerator()) {
  Write-FixtureFile ("src/" + $source.Key + '.cs') ("namespace OrderDesk;`n`n" + $source.Value + "`n")
}
foreach ($kind in 'api','worker','audit') {
  $lines = [Collections.Generic.List[string]]::new()
  for ($index = 0; $index -lt 5000; $index++) {
    $time = [DateTimeOffset]::Parse('2026-10-14T00:00:00Z').AddSeconds($index * 3).ToString('O')
    $lines.Add("$time INFO component=$kind trace=ordinary-$index tenant=loja-utc operation=history rows=12 status=ok elapsedMs=8")
    if ($index -eq 2100) {
      switch ($kind) {
        api { $lines.Add('2026-10-14T06:00:00Z WARN trace=inc241-history tenant=loja-sul requestedDate=2026-10-13 zone=America/Sao_Paulo startUtc=2026-10-13T00:00:00Z endUtc=2026-10-14T00:00:00Z rows=17 expectedRows=20 cache=bypass version=2.18') }
        worker { $lines.Add('2026-10-14T06:01:00Z WARN trace=inc241-export tenant=loja-sul requestedDate=2026-10-13 startUtc=2026-10-13T00:00:00Z endUtc=2026-10-14T00:00:00Z rows=17 completed=true') }
        audit {
          $lines.Add('2026-10-14T02:10:00Z INFO trace=inc241-history order=ORD-4831 tenant=loja-sul createdAtUtc=2026-10-14T02:10:00Z operation=insert committed=true')
          $lines.Add('2026-10-14T02:30:00Z INFO trace=inc241-history order=ORD-4832 tenant=loja-sul createdAtUtc=2026-10-14T02:30:00Z operation=insert committed=true')
          $lines.Add('2026-10-14T02:50:00Z INFO trace=inc241-history order=ORD-4833 tenant=loja-sul createdAtUtc=2026-10-14T02:50:00Z operation=insert committed=true')
        }
      }
    }
  }
  Write-FixtureFile ("logs/$kind.log") ($lines -join "`n")
}
$files = @(Get-ChildItem -LiteralPath $root -Recurse -File)
if ($files.Count -lt 30 -or $sources.Count -lt 30) { throw 'Fixture is smaller than requested.' }
[pscustomobject]@{files=$files.Count;codeFiles=$sources.Count;bytes=($files|Measure-Object Length -Sum).Sum;root=$root}
