# Runs in a clean Windows STA process before publishing an update.
param([Parameter(Mandatory=$true)][string]$Exe)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml
$assembly = [Reflection.Assembly]::LoadFile((Resolve-Path $Exe).Path)
$desktop = [Activator]::CreateInstance($assembly.GetType('FormatFlow.DesktopWindow', $true))
$window = $desktop.Window
$hero = $window.FindName('Hero')
$top = $window.FindName('AdTop')
$lower = $window.FindName('AdSearch')
$content = $window.FindName('ContentPanel')
$queue = $window.FindName('QueueSection')
if ($top.Parent -ne $hero -or $hero.Children[0] -ne $top) { throw 'Top banner is not immediately above the greeting' }
if ($content.Children.IndexOf($lower) -le $content.Children.IndexOf($queue)) { throw 'Lower banner must follow file cards' }
if ($window.FindName('AdBottom')) { throw 'Unexpected third advertisement' }
if ($top.MaxWidth -ge $lower.MaxWidth) { throw 'Top banner must be narrower' }
# Exercise the real WPF advertisement rendering when the owner removes or restores a slot.
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$apply = $desktop.GetType().GetMethod('ApplyChannelState', $flags)
$feedType = $assembly.GetType('FormatFlow.ChannelFeed', $true)
$read = $assembly.GetType('FormatFlow.Json', $true).GetMethod('Read').MakeGenericMethod(@($feedType))
$normalize = $assembly.GetType('FormatFlow.ChannelRules', $true).GetMethod('Normalize')
function Set-TestFeed([string]$json) {
  $feed = $read.Invoke($null, @($json))
  $feed = $normalize.Invoke($null, @($feed))
  [void]$apply.Invoke($desktop, @($feed, $true))
}
Set-TestFeed '{"schema":1,"ads":[{"title":"Top","placement":"top"},{"title":"Bottom","placement":"belowSearch"}]}'
if ($top.Visibility -ne [System.Windows.Visibility]::Visible -or $lower.Visibility -ne [System.Windows.Visibility]::Visible) { throw 'Both configured banners must appear' }
Set-TestFeed '{"schema":1,"ads":[{"title":"Bottom","placement":"belowSearch"}]}'
if ($top.Visibility -ne [System.Windows.Visibility]::Collapsed -or $top.Content -ne $null) { throw 'Removed top banner must disappear' }
if ($lower.Visibility -ne [System.Windows.Visibility]::Visible) { throw 'Removing top must preserve lower banner' }
Set-TestFeed '{"schema":1,"ads":[]}'
if ($top.Visibility -ne [System.Windows.Visibility]::Collapsed -or $lower.Visibility -ne [System.Windows.Visibility]::Collapsed) { throw 'Both removed banners must disappear' }
Set-TestFeed '{"schema":1,"ads":[{"title":"Top again","placement":"top"}]}'
if ($top.Visibility -ne [System.Windows.Visibility]::Visible -or $lower.Visibility -ne [System.Windows.Visibility]::Collapsed) { throw 'Top banner must be restorable independently' }
# Stale advertisements must not be replayed when a computer starts offline.
$cachedFeed = $read.Invoke($null, @('{"schema":1,"revision":6,"ads":[{"title":"Old top","placement":"top"},{"title":"Old lower","placement":"belowSearch"}]}'))
$cachedFeed = $normalize.Invoke($null, @($cachedFeed))
[void]$apply.Invoke($desktop, @($cachedFeed, $false))
if ($top.Visibility -ne [System.Windows.Visibility]::Collapsed -or $lower.Visibility -ne [System.Windows.Visibility]::Collapsed) { throw 'Startup must not replay cached advertisements' }
Set-TestFeed '{"schema":1,"ads":[{"title":"Top","placement":"top"},{"title":"Lower","placement":"belowSearch"}]}'
[void]$desktop.GetType().GetMethod('PublisherUnavailable', $flags).Invoke($desktop, @('Simulated offline connection'))
if ($top.Visibility -ne [System.Windows.Visibility]::Collapsed -or $lower.Visibility -ne [System.Windows.Visibility]::Collapsed) { throw 'A failed refresh must not retain stale advertisements' }
Write-Output 'Windows WPF construction, banner removal and offline startup passed.'

# Exercise the production feed reader against the live service.
$channel = [Activator]::CreateInstance($assembly.GetType('FormatFlow.ChannelService', $true))
$feedResult = $channel.CheckAsync([Threading.CancellationToken]::None, $false, $false).GetAwaiter().GetResult()
if ($null -eq $feedResult -or $feedResult.State.schema -ne 1 -or $feedResult.State.revision -lt 1) { throw 'Live publisher feed check failed' }
Write-Output ("Live publisher feed revision: " + $feedResult.State.revision)

# Exercise the compatibility reader even if CI's native HTTP connection works.
$compatibilityPath = [IO.Path]::Combine([IO.Path]::GetTempPath(), [Guid]::NewGuid().ToString() + '.json')
try {
  $reader = $channel.GetType().GetMethod('WindowsFeedAsync', [Reflection.BindingFlags]'Static,NonPublic')
  $readTask = $reader.Invoke($null, @('https://raw.githubusercontent.com/yosef-chadad/formatflow-control/main/feed.json', $compatibilityPath, [long]262144, [Threading.CancellationToken]::None))
  $readTask.GetAwaiter().GetResult()
  $compatibilityFeed = Get-Content -Raw $compatibilityPath | ConvertFrom-Json
  if ($compatibilityFeed.schema -ne 1) { throw 'Windows compatibility reader returned invalid data' }
  Write-Output ('Windows compatibility reader revision: ' + $compatibilityFeed.revision)
} finally { if (Test-Path $compatibilityPath) { Remove-Item $compatibilityPath } }

# Reproduce revision 6 and run the real asynchronous startup with its dispatcher.
$cachePath = $channel.GetType().GetField('CachePath').GetValue($null)
[IO.File]::WriteAllText($cachePath, '{"schema":1,"revision":6,"ads":[{"title":"Old top","placement":"top"},{"title":"Old lower","placement":"belowSearch"}]}')
$startup = $desktop.GetType().GetMethod('InitializeChannel', $flags).Invoke($desktop, @())
if ($top.Visibility -ne [System.Windows.Visibility]::Collapsed) { throw 'Revision-6 startup briefly showed an old top advertisement' }
$frame = New-Object System.Windows.Threading.DispatcherFrame
$deadline = [DateTime]::UtcNow.AddSeconds(90)
$poll = New-Object System.Windows.Threading.DispatcherTimer
$poll.Interval = [TimeSpan]::FromMilliseconds(50)
$poll.Add_Tick({ if ($startup.IsCompleted -or [DateTime]::UtcNow -gt $deadline) { $frame.Continue = $false } })
$poll.Start()
[System.Windows.Threading.Dispatcher]::PushFrame($frame)
$poll.Stop()
if (-not $startup.IsCompleted) { throw 'Asynchronous startup timed out' }
$startup.GetAwaiter().GetResult()
$savedFeed = Get-Content -Raw $cachePath | ConvertFrom-Json
if ($savedFeed.revision -le 6) { throw 'Startup did not replace the stale revision-6 cache' }
$expectedTop = @($savedFeed.ads | Where-Object { $_.placement -eq 'top' }).Count -gt 0
if (($top.Visibility -eq [System.Windows.Visibility]::Visible) -ne $expectedTop) { throw 'Displayed top advertisement does not match saved live configuration' }
if ($window.FindName('PublisherStatus').Text -notlike "*($($savedFeed.revision))*") { throw 'Successful startup did not show its feed revision' }
$window.Close()
Write-Output ('Revision-6 cache replaced successfully with revision ' + $savedFeed.revision)

