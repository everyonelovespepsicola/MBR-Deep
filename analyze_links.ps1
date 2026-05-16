Add-Type -AssemblyName PresentationFramework

[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="MBR-Deep .lnk Analyzer" Height="600" Width="800" Background="#1E1E1E" WindowStartupLocation="CenterScreen">
    <Grid Margin="15">
        <Grid.RowDefinitions>
            <RowDefinition Height="120"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <Border Name="DropZone" Grid.Row="0" BorderBrush="#0078D7" BorderThickness="3" CornerRadius="10" Background="#2D2D30" AllowDrop="True" Cursor="Hand">
            <TextBlock Text="Drag and Drop the God Mode shortcuts folder here!"
                       Foreground="White" FontSize="20" FontWeight="SemiBold"
                       HorizontalAlignment="Center" VerticalAlignment="Center" IsHitTestVisible="False"/>
        </Border>

        <TextBox Name="OutputBox" Grid.Row="1" Margin="0,15,0,0"
                 Background="#2D2D30" Foreground="#D4D4D4" FontFamily="Consolas" FontSize="14"
                 AcceptsReturn="True" VerticalScrollBarVisibility="Auto" TextWrapping="Wrap" />
    </Grid>
</Window>
"@

$reader = (New-Object System.Xml.XmlNodeReader $xaml)
$window = [Windows.Markup.XamlReader]::Load($reader)

$dropZone = $window.FindName("DropZone")
$outputBox = $window.FindName("OutputBox")

$dropZone.Add_Drop({
        param($sender, $e)
        if ($e.Data.GetDataPresent([Windows.DataFormats]::FileDrop)) {
            $files = $e.Data.GetData([Windows.DataFormats]::FileDrop)
            $lnkFiles = @()

            foreach ($file in $files) {
                if (Test-Path $file -PathType Container) {
                    $lnkFiles += Get-ChildItem -Path $file -Filter "*.lnk" -File | Select-Object -ExpandProperty FullName
                }
                elseif ($file -match "\.lnk$") {
                    $lnkFiles += $file
                }
            }

            if ($lnkFiles.Count -eq 0) {
                $outputBox.Text = "No .lnk files found. Please drop a folder containing shortcuts."
                return
            }

            # Only analyze the first 5 to prevent UI freezing / massive text dumps
            $analyzeCount = [Math]::Min($lnkFiles.Count, 5)
            $outputBox.Text = "Found $($lnkFiles.Count) shortcuts. Analyzing the first $analyzeCount...`r`n`r`n"

            $wshShell = New-Object -ComObject WScript.Shell
            $shellApp = New-Object -ComObject Shell.Application

            for ($i = 0; $i -lt $analyzeCount; $i++) {
                $lnk = $lnkFiles[$i]
                $fileName = Split-Path $lnk -Leaf
                $outputBox.AppendText("=== $fileName ===`r`n")

                # WScript.Shell Output
                $shortcut = $wshShell.CreateShortcut($lnk)
                $target = if ([string]::IsNullOrWhiteSpace($shortcut.TargetPath)) { "<BLANK/VIRTUAL>" } else { $shortcut.TargetPath }
                $args = if ([string]::IsNullOrWhiteSpace($shortcut.Arguments)) { "<BLANK>" } else { $shortcut.Arguments }

                $outputBox.AppendText(" [WScript.Shell API]`r`n")
                $outputBox.AppendText("   TargetPath : $target`r`n")
                $outputBox.AppendText("   Arguments  : $args`r`n`r`n")
            }
        }
    })

# Visual feedback for Drag Enter/Leave
$dropZone.Add_DragEnter({
        $dropZone.Background = "#3E3E42"
        $dropZone.BorderBrush = "#569CD6"
    })
$dropZone.Add_DragLeave({
        $dropZone.Background = "#2D2D30"
        $dropZone.BorderBrush = "#0078D7"
    })

$window.ShowDialog() | Out-Null
