Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

# --- XAML UI Definition ---
[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Installer File Capture Utility" Height="550" Width="800" Background="#F0F2F5" WindowStartupLocation="CenterScreen"
        AllowDrop="True">
    <Grid Margin="15">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Header / Target File Status -->
        <Border Grid.Row="0" Background="#FFFFFF" CornerRadius="5" Padding="15" Margin="0,0,0,15" BorderBrush="#DCDCDC" BorderThickness="1">
            <StackPanel>
                <TextBlock Text="Target Installer:" FontWeight="Bold" FontSize="12" Foreground="#444444"/>
                <TextBlock x:Name="TxtTargetExe" Text="No file selected. Drag &amp; Drop an EXE onto this script or click Browse."
                           FontSize="14" Foreground="#0066CC" TextTrimming="CharacterEllipsis" Margin="0,5,0,0"/>
            </StackPanel>
        </Border>

        <!-- Actions and Progress -->
        <Grid Grid.Row="1" Margin="0,0,0,15">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <Button x:Name="BtnBrowse" Content="Browse EXE" Grid.Column="0" Width="100" Height="32" Background="#E1E1E1" BorderThickness="0" Cursor="Hand"/>
            <TextBlock x:Name="TxtStatus" Text="Ready to scan..." Grid.Column="1" VerticalAlignment="Center" Margin="15,0" FontSize="12" FontWeight="SemiBold" Foreground="#666666"/>
            <Button x:Name="BtnStart" Content="Run &amp; Capture" Grid.Column="2" Width="140" Height="32" Background="#0078D7" Foreground="White" FontWeight="Bold" BorderThickness="0" IsEnabled="False" Cursor="Hand"/>
        </Grid>

        <!-- Results Display -->
        <GroupBox Grid.Row="2" Header="Captured File Modifications (Created Files)" FontSize="12" Foreground="#444444">
            <ListView x:Name="LstResults" Margin="5" Background="White" BorderBrush="#E0E0E0">
                <ListView.View>
                    <GridView>
                        <GridViewColumn Header="Type" DisplayMemberBinding="{Binding Type}" Width="60"/>
                        <GridViewColumn Header="Full Path" DisplayMemberBinding="{Binding Path}" Width="680"/>
                    </GridView>
                </ListView.View>
            </ListView>
        </GroupBox>

        <!-- Footer Footer -->
        <TextBlock Grid.Row="3" Text="Monitors: Program Files, AppData (Local/Roaming), and ProgramData" FontSize="11" Foreground="#888888" Margin="0,10,0,0" HorizontalAlignment="Center"/>
    </Grid>
</Window>
"@

# --- Load XAML Windows ---
$reader = New-Object System.Xml.XmlNodeReader($xaml)
$Form = [Windows.Markup.XamlReader]::Load($reader)

# --- Map UI Elements ---
$TxtTargetExe = $Form.FindName("TxtTargetExe")
$TxtStatus = $Form.FindName("TxtStatus")
$BtnBrowse = $Form.FindName("BtnBrowse")
$BtnStart = $Form.FindName("BtnStart")
$LstResults = $Form.FindName("LstResults")

# --- Monitored Pathways Scope ---
# Scanning the entire C:\ drive takes too long. We target the standard install locations for speed.
$ScanPaths = @(
    $env:ProgramFiles,
    ${env:ProgramFiles(x86)},
    $env:LOCALAPPDATA,
    $env:AppData,
    $env:ProgramData
)

# --- Helper: Take Snapshot ---
Function Take-SystemSnapshot {
    Param([string]$StateText)
    $Script:TxtStatus.Text = "$StateText... (Analyzing System Layout)"
    [System.Windows.Forms.Application]::DoEvents()

    $Snapshot = @{}
    foreach ($Path in $ScanPaths) {
        if (Test-Path $Path) {
            Get-ChildItem -Path $Path -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
                $Snapshot[$_.FullName] = $_.PsIsContainer
            }
        }
    }
    return $Snapshot
}

# --- Target File Initialization (Supports Drag and Drop drop-in) ---
$TargetExePath = $args[0]
if ($TargetExePath -and (Test-Path $TargetExePath) -and (Get-Item $TargetExePath).Extension -eq ".exe") {
    $TxtTargetExe.Text = $TargetExePath
    $BtnStart.IsEnabled = $true
    $TxtStatus.Text = "Target dropped in successfully. Ready to run."
}

# --- Event: Drag and Drop ---
$Form.Add_PreviewDragOver({
        param($sender, $e)
        if ($e.Data.GetDataPresent([System.Windows.DataFormats]::FileDrop)) {
            $e.Effects = [System.Windows.DragDropEffects]::Copy
        }
        else {
            $e.Effects = [System.Windows.DragDropEffects]::None
        }
        $e.Handled = $true
    })

$Form.Add_Drop({
        param($sender, $e)
        if ($e.Data.GetDataPresent([System.Windows.DataFormats]::FileDrop)) {
            $files = $e.Data.GetData([System.Windows.DataFormats]::FileDrop)
            if ($files -and $files.Count -gt 0 -and $files[0].EndsWith(".exe", [System.StringComparison]::OrdinalIgnoreCase)) {
                $Script:TargetExePath = $files[0]
                $TxtTargetExe.Text = $Script:TargetExePath
                $BtnStart.IsEnabled = $true
                $TxtStatus.Text = "Target dropped in successfully. Ready to run."
            }
            else {
                $TxtStatus.Text = "Please drop a valid .exe file."
            }
        }
    })

# --- Event: Browse Button ---
$BtnBrowse.Add_Click({
        Add-Type -AssemblyName System.Windows.Forms
        $FileBrowser = New-Object System.Windows.Forms.OpenFileDialog
        $FileBrowser.Filter = "Executable Files (*.exe)|*.exe"
        $FileBrowser.InitialDirectory = "$env:USERPROFILE\Downloads"

        if ($FileBrowser.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
            $Script:TargetExePath = $FileBrowser.FileName
            $TxtTargetExe.Text = $Script:TargetExePath
            $BtnStart.IsEnabled = $true
            $TxtStatus.Text = "Ready to analyze installer."
        }
    })

# --- Event: Start Tracking Execution ---
$BtnStart.Add_Click({
        if (-not (Test-Path $Script:TargetExePath)) { return }

        $BtnStart.IsEnabled = $false
        $BtnBrowse.IsEnabled = $false
        $LstResults.Items.Clear()

        # 1. Capture Pre-Installation State
        $BeforeState = Take-SystemSnapshot -StateText "Step 1/3: Taking Initial State Snapshot"

        # 2. Execute Installer and Wait
        $TxtStatus.Text = "Step 2/3: Executing Installer... Waiting for close."
        [System.Windows.Forms.Application]::DoEvents()

        $InstallerProcess = Start-Process -FilePath $Script:TargetExePath -PassThru -Wait

        # 3. Capture Post-Installation State
        $AfterState = Take-SystemSnapshot -StateText "Step 3/3: Taking Post-Install Snapshot"

        # 4. Compute Structural Diffs
        $TxtStatus.Text = "Comparing changes..."
        [System.Windows.Forms.Application]::DoEvents()

        foreach ($Key in $AfterState.Keys) {
            if (-not $BeforeState.ContainsKey($Key)) {
                $ItemType = if ($AfterState[$Key]) { "DIR" } else { "FILE" }
                $LstResults.Items.Add([PSCustomObject]@{
                        Type = $ItemType
                        Path = $Key
                    })
            }
        }

        $TxtStatus.Text = "Analysis complete! $(($LstResults.Items).Count) entries captured."
        $BtnBrowse.IsEnabled = $true
    })

# --- Execution Entry ---
$Form.ShowDialog() | Out-Null
