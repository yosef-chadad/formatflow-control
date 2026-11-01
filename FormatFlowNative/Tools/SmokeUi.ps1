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
$window.Close()
Write-Output 'Windows WPF construction and two-banner structure passed.'
