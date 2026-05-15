Write-Host "Setting up MBR-Deep App Drawer Build..." -ForegroundColor Cyan

$wpfDir = "src\UI_WPF"
$projectName = "AppDrawerXAML"
$csprojPath = Join-Path $wpfDir "$projectName.csproj"
$appXamlPath = Join-Path $wpfDir "App.xaml"
$appXamlCsPath = Join-Path $wpfDir "App.xaml.cs"

# 1. Generate the .NET 10 Project File dynamically
if (-not (Test-Path $csprojPath)) {
  Write-Host " -> Creating $csprojPath (Targeting .NET 10 LTS)..." -ForegroundColor Green
  @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationIcon>..\..\icon3.ico</ApplicationIcon>
  </PropertyGroup>
  <ItemGroup>
    <Compile Remove="vcpkg\**" />
    <Compile Remove=".env\**" />
    <!-- This ensures your C-Engine DLL is copied directly to the output folder -->
    <None Include="..\Engine\fast_search.dll">
      <Link>fast_search.dll</Link>
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
"@ | Out-File -FilePath $csprojPath -Encoding ascii
}

# 2. Generate the Application Entry Point (App.xaml & App.xaml.cs)
if (-not (Test-Path $appXamlPath)) {
  Write-Host " -> Creating App.xaml (WPF Entry Point)..." -ForegroundColor Green
  @"
<Application x:Class="AppDrawerXAML.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="AppDrawerWindow.xaml">
    <Application.Resources>
    </Application.Resources>
</Application>
"@ | Out-File -FilePath $appXamlPath -Encoding ascii

  @"
using System.Windows;
namespace AppDrawerXAML { public partial class App : System.Windows.Application { } }
"@ | Out-File -FilePath $appXamlCsPath -Encoding ascii
}

# 3. Compile and Launch!
Write-Host "`nBuilding and launching the App Drawer..." -ForegroundColor Cyan
Write-Host "----------------------------------------"
dotnet run --project $csprojPath
