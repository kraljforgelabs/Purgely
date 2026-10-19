# Purgely – window changes for a Windows 11 look

Mechanical XAML replacements that bring MainWindow, ScanResultsWindow, StartupWindow (and, if time allows,
TrialWindow, TimelineWindow, UndoWindow, RelatedWindow) in line with the new HomeWindow.

What every window gets:

- **Page header**: title in the Windows 11 "Title" ramp (28, SemiBold) plus a one-line subtitle in the secondary text colour. No emoji in titles.
- **Fluent icons instead of emoji** on buttons (Segoe Fluent Icons glyph + text label), with `AutomationProperties.Name` so screen readers read the label, not the glyph.
- **One accent button per window** for the primary action (`AccentButton`); everything else stays a normal Fluent button.
- **The list sits in a card** (`ListCard`): rounded 8 px, translucent fill, 1 px hairline border. The Fluent DataGrid stays in charge of rows, hover and selection; we only set `Background="Transparent"`, `BorderThickness="0"`, `MinRowHeight="36"` and a subtle column header (`GridHeader`).
- **InfoBar instead of plain or red warning text** (`InfoBarInfo` / `InfoBarWarning` / `InfoBarSuccess` / `InfoBarError`).
- **Busy overlay** as a dark scrim with a centred rounded card.
- **Spacing**: 20 px page margin, 16 px under the header, 12 px between the other sections, 8 px between buttons.

Guarantees (checked by a script that applied every change below to a copy of the current files):

- Each "Replace" snippet is copied exactly from the current file and occurs **exactly once** at the moment you apply it. Apply the changes **in the order given** within each file.
- Every `x:Name`, every event handler (`Click`, `TextChanged`, `Checked`, ...) and every `{Binding ...}` from the original is still present. **No code-behind change is needed.**
- Every `{StaticResource ...}` used is defined in `Theme.xaml` or in the window itself.
- Every result is well-formed XML. (The project could not be compiled in this environment – no .NET SDK – so do a build and a quick light/dark click-through on Windows.)
- No style is `BasedOn` a Fluent style. The only `BasedOn` used is on our own `TextBlock`/`Border` styles from `Theme.xaml` (`GridText`, `GridWrapText`, `InfoBarInfo`).

Ready-made results: `design2/applied/` holds each window with all changes already applied (made from the files as they were on 2026-10-15, 22:57). Copying those over `src` is equivalent to applying everything below, **as long as nobody has edited the source files since then**. If they have, apply the snippets instead.

## Step 0 – add the theme (required before any window change)

1. Copy `design2/Theme.xaml` to `src/Purgely/Theme.xaml`. The WPF SDK picks it up as a Page automatically; no `.csproj` change.
2. Apply the `App.xaml` change below.
3. Build and run once **before** touching the windows: everything must look exactly as before (Theme.xaml only adds keyed resources; it changes nothing on its own).

If the Fluent look disappears after step 2 (it should not on .NET 9/10, but `ThemeMode` + `Application.Resources` had teething problems), undo the App.xaml change and merge from code instead, in `App.xaml.cs`:

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Theme.xaml", UriKind.Relative) });
    base.OnStartup(e);
}
```

(If `App.xaml.cs` already overrides `OnStartup`, add the first line at its top.)

HomeWindow needs no change. It defines `AccentBrush`, `CardBrush`, `IconFont` etc. locally with the same values as `Theme.xaml`; the local copies win and look identical. They can be deleted from HomeWindow later.

## Icon map (Segoe Fluent Icons)

Matches the glyphs already used on the HomeWindow tiles.

| Action | Glyph | Name |
|---|---|---|
| Uninstall / Delete / Discard | `&#xE74D;` | Delete |
| Scan leftovers | `&#xE721;` | Search |
| Find leftovers by name | `&#xE71E;` | Zoom |
| Update (one program) | `&#xE896;` | Download |
| Installer junk | `&#xE7B8;` | Package |
| Refresh | `&#xE72C;` | Refresh |
| Startup | `&#xE945;` | LightningBolt |
| Trial Mode | `&#xEA18;` | Shield |
| Timeline | `&#xE81C;` | History |
| Undo center / Undo / Revert | `&#xE7A7;` | Undo |
| Restore | `&#xE777;` | UpdateRestore |
| Optimize | `&#xEC4A;` | SpeedHigh |
| Turn off | `&#xE7E8;` | PowerButton |
| Turn on / Start | `&#xE768;` | Play |
| Start later | `&#xE916;` | Stopwatch |
| Keep off | `&#xE72E;` | Lock |
| Open location / folder | `&#xE838;` | FolderOpen |
| Select recommended | `&#xE8FB;` | Accept |
| Select none | `&#xE8E6;` | ClearSelection |
| Keep / Done | `&#xE73E;` | CheckMark |
| Analyze | `&#xE9D9;` | Diagnostic |
| Details / Info | `&#xE946;` | Info |
| Warning (InfoBar) | `&#xE7BA;` | Warning |
| Error (InfoBar) | `&#xEA39;` | ErrorBadge |

Button pattern used everywhere:

```xml
<Button Click="X_Click" Padding="12,6" AutomationProperties.Name="Label">
    <StackPanel Orientation="Horizontal">
        <TextBlock Style="{StaticResource IconText}" Text="&#xE74D;"/>
        <TextBlock Style="{StaticResource ButtonLabel}" Text="Label"/>
    </StackPanel>
</Button>
```

Note: a button whose `Content` is set from code-behind (`ScanResultsWindow.RemoveButton`) stays text-only, because the code would overwrite the icon.

---

## App.xaml  (Step 0 – required first)

### App.xaml – change 1: Merge Theme.xaml into the application resources

Replace:

```xml
             ThemeMode="System">
</Application>
```

With:

```xml
             ThemeMode="System">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Theme.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

---

## MainWindow.xaml  (Priority 1)

### MainWindow.xaml – change 1: Page margin 20 (was 20,16,20,12)

Replace:

```xml
    <Grid Margin="20,16,20,12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Header -->
```

With:

```xml
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Header -->
```

### MainWindow.xaml – change 2: Page header: title + one-line subtitle; emoji nav buttons become Fluent icon + text; admin badge as a pill

Replace:

```xml
        <DockPanel Grid.Row="0" Margin="0,0,0,14">
            <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Center">
                <Button Content="⚡  Startup" Click="Startup_Click" Padding="14,6" Margin="0,0,8,0"/>
                <Button Content="🧪  Trial Mode" Click="Trial_Click" Padding="14,6" Margin="0,0,8,0"/>
                <Button Content="🕒  Timeline" Click="Timeline_Click" Padding="14,6" Margin="0,0,8,0"/>
                <Button Content="↶  Undo center" Click="Undo_Click" Padding="14,6" Margin="0,0,16,0"/>
                <TextBlock x:Name="AdminBadge" VerticalAlignment="Center" FontWeight="SemiBold"/>
            </StackPanel>
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="Purgely" FontSize="30" FontWeight="SemiBold"/>
                <TextBlock Text="smart uninstaller" FontSize="15" Opacity="0.65"
                           VerticalAlignment="Bottom" Margin="12,0,0,6"/>
            </StackPanel>
        </DockPanel>
```

With:

```xml
        <DockPanel Grid.Row="0" Margin="0,0,0,16">
            <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Top">
                <Border Style="{StaticResource Badge}" Margin="0,0,12,0">
                    <TextBlock x:Name="AdminBadge" Style="{StaticResource CaptionText}"/>
                </Border>
                <Button Click="Startup_Click" Padding="12,6" Margin="0,0,8,0" AutomationProperties.Name="Startup">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE945;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Startup"/>
                    </StackPanel>
                </Button>
                <Button Click="Trial_Click" Padding="12,6" Margin="0,0,8,0" AutomationProperties.Name="Trial Mode">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xEA18;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Trial Mode"/>
                    </StackPanel>
                </Button>
                <Button Click="Timeline_Click" Padding="12,6" Margin="0,0,8,0" AutomationProperties.Name="Timeline">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE81C;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Timeline"/>
                    </StackPanel>
                </Button>
                <Button Click="Undo_Click" Padding="12,6" AutomationProperties.Name="Undo center">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE7A7;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Undo center"/>
                    </StackPanel>
                </Button>
            </StackPanel>
            <StackPanel>
                <TextBlock Text="Uninstall programs" Style="{StaticResource PageTitle}"/>
                <TextBlock Style="{StaticResource PageSubtitle}"
                           Text="Select a program to uninstall it, update it or clean up what it left behind. Double-click to uninstall."/>
            </StackPanel>
        </DockPanel>
```

### MainWindow.xaml – change 3: Search row: TextBox with placeholder instead of a 'Search' label; secondary text style for the hint

Replace:

```xml
        <DockPanel Grid.Row="1" Margin="0,0,0,10">
            <TextBlock Text="Search" VerticalAlignment="Center" Margin="0,0,10,0"/>
            <TextBox x:Name="SearchBox" Width="340" TextChanged="Search_TextChanged"
                     ToolTip="Just start typing, or press Ctrl+F. Ctrl+V pastes here. Esc clears."/>
            <CheckBox x:Name="OrphansOnly" Content="Removed programs only (leftovers)"
                      Margin="20,0,0,0" VerticalAlignment="Center"
                      Checked="Filter_Changed" Unchecked="Filter_Changed"/>
            <CheckBox x:Name="UpdatesOnly" Content="Only with updates"
                      Margin="20,0,0,0" VerticalAlignment="Center"
                      Checked="Filter_Changed" Unchecked="Filter_Changed"/>
            <TextBlock x:Name="StatusHint" Margin="20,0,0,0" VerticalAlignment="Center" Opacity="0.7" TextTrimming="CharacterEllipsis"/>
        </DockPanel>
```

With:

```xml
        <DockPanel Grid.Row="1" Margin="0,0,0,12">
            <Grid Width="340">
                <TextBox x:Name="SearchBox" TextChanged="Search_TextChanged"
                         AutomationProperties.Name="Search programs"
                         ToolTip="Just start typing, or press Ctrl+F. Ctrl+V pastes here. Esc clears."/>
                <TextBlock Text="Search programs" Margin="12,0,0,0" VerticalAlignment="Center" IsHitTestVisible="False"
                           Foreground="{DynamicResource TextFillColorSecondaryBrush}">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock">
                            <Setter Property="Visibility" Value="Collapsed"/>
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding Text.Length, ElementName=SearchBox}" Value="0">
                                    <Setter Property="Visibility" Value="Visible"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
            </Grid>
            <CheckBox x:Name="OrphansOnly" Content="Removed programs only (leftovers)"
                      Margin="16,0,0,0" VerticalAlignment="Center"
                      Checked="Filter_Changed" Unchecked="Filter_Changed"/>
            <CheckBox x:Name="UpdatesOnly" Content="Only with updates"
                      Margin="16,0,0,0" VerticalAlignment="Center"
                      Checked="Filter_Changed" Unchecked="Filter_Changed"/>
            <TextBlock x:Name="StatusHint" Margin="16,0,0,0" Style="{StaticResource SecondaryText}"/>
        </DockPanel>
```

### MainWindow.xaml – change 4: Program list inside a card; Fluent DataGrid tuned inline (row height 36, transparent, subtle header)

Replace:

```xml
        <DataGrid x:Name="ProgramsGrid" Grid.Row="2" AutoGenerateColumns="False" IsReadOnly="True"
                  SelectionMode="Single" HeadersVisibility="Column" GridLinesVisibility="None"
                  CanUserResizeRows="False" MouseDoubleClick="ProgramsGrid_MouseDoubleClick">
            <DataGrid.Resources>
                <Style x:Key="OrphanText" TargetType="TextBlock">
```

With:

```xml
        <Border Grid.Row="2" Style="{StaticResource ListCard}">
        <DataGrid x:Name="ProgramsGrid" AutoGenerateColumns="False" IsReadOnly="True"
                  SelectionMode="Single" HeadersVisibility="Column" GridLinesVisibility="None"
                  CanUserResizeRows="False" MouseDoubleClick="ProgramsGrid_MouseDoubleClick"
                  Background="Transparent" BorderThickness="0" MinRowHeight="36"
                  ColumnHeaderStyle="{StaticResource GridHeader}">
            <DataGrid.Resources>
                <Style x:Key="OrphanText" TargetType="TextBlock" BasedOn="{StaticResource GridText}">
```

### MainWindow.xaml – change 5: Attention colour from the palette

Replace:

```xml
                            <Setter Property="Foreground" Value="#D97706"/>
```

With:

```xml
                            <Setter Property="Foreground" Value="{StaticResource AttentionBrush}"/>
```

### MainWindow.xaml – change 6: Columns: same cell padding everywhere (GridText); close the card Border

Replace:

```xml
                <DataGridTextColumn Header="Publisher" Binding="{Binding Publisher}" Width="2*"/>
                <DataGridTextColumn Header="Version" Binding="{Binding Version}" Width="*"/>
                <DataGridTextColumn Header="Size" Binding="{Binding SizeText}" SortMemberPath="SizeKb" Width="90"/>
                <DataGridTextColumn Header="Installed" Binding="{Binding InstallDateText}" SortMemberPath="InstallDate" Width="100"/>
                <DataGridTextColumn Header="Update" Binding="{Binding UpdateText}" Width="130"/>
                <DataGridTextColumn Header="Status" Binding="{Binding OrphanReason}" Width="2*" ElementStyle="{StaticResource OrphanText}"/>
                <DataGridTextColumn Header="Dependencies" Binding="{Binding UsedByText}" Width="2*">
                    <DataGridTextColumn.ElementStyle>
                        <Style TargetType="TextBlock">
                            <Setter Property="ToolTip" Value="{Binding UsedByTooltip}"/>
                        </Style>
                    </DataGridTextColumn.ElementStyle>
                </DataGridTextColumn>
            </DataGrid.Columns>
        </DataGrid>
```

With:

```xml
                <DataGridTextColumn Header="Publisher" Binding="{Binding Publisher}" Width="2*" ElementStyle="{StaticResource GridText}"/>
                <DataGridTextColumn Header="Version" Binding="{Binding Version}" Width="*" ElementStyle="{StaticResource GridText}"/>
                <DataGridTextColumn Header="Size" Binding="{Binding SizeText}" SortMemberPath="SizeKb" Width="90" ElementStyle="{StaticResource GridText}"/>
                <DataGridTextColumn Header="Installed" Binding="{Binding InstallDateText}" SortMemberPath="InstallDate" Width="100" ElementStyle="{StaticResource GridText}"/>
                <DataGridTextColumn Header="Update" Binding="{Binding UpdateText}" Width="130" ElementStyle="{StaticResource GridText}"/>
                <DataGridTextColumn Header="Status" Binding="{Binding OrphanReason}" Width="2*" ElementStyle="{StaticResource OrphanText}"/>
                <DataGridTextColumn Header="Dependencies" Binding="{Binding UsedByText}" Width="2*">
                    <DataGridTextColumn.ElementStyle>
                        <Style TargetType="TextBlock" BasedOn="{StaticResource GridText}">
                            <Setter Property="ToolTip" Value="{Binding UsedByTooltip}"/>
                        </Style>
                    </DataGridTextColumn.ElementStyle>
                </DataGridTextColumn>
            </DataGrid.Columns>
        </DataGrid>
        </Border>
```

### MainWindow.xaml – change 7: Command row: Uninstall is the accent (primary) button; icon + text on every action; Refresh becomes icon-only

Replace:

```xml
        <DockPanel Grid.Row="3" Margin="0,12,0,0">
            <StackPanel Orientation="Horizontal" DockPanel.Dock="Left">
                <Button Content="Uninstall" Click="Uninstall_Click" Padding="18,6" FontWeight="SemiBold"/>
                <Button Content="Scan leftovers" Click="Scan_Click" Padding="18,6" Margin="8,0,0,0"/>
                <Button Content="Find leftovers by name" Click="ScanByName_Click" Padding="18,6" Margin="8,0,0,0"
                        ToolTip="Type the name of an already removed program in the Search box, then click here"/>
                <Button Content="Update" Click="Update_Click" Padding="18,6" Margin="8,0,0,0"
                        ToolTip="Update the selected program with winget (Windows Package Manager)"/>
                <Button Content="Installer junk" Click="Junk_Click" Padding="18,6" Margin="8,0,0,0"
                        ToolTip="Find cached installers that no program uses any more"/>
                <Button Content="Refresh" Click="Refresh_Click" Padding="18,6" Margin="8,0,0,0"/>
            </StackPanel>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Center">
                <StackPanel x:Name="BusyPanel" Orientation="Horizontal" Visibility="Collapsed" Margin="0,0,18,0">
                    <ProgressBar x:Name="BusyBar" Width="150" Height="6" Minimum="0" Maximum="100"
                                 VerticalAlignment="Center"/>
                    <TextBlock x:Name="BusyText" Margin="10,0,0,0" VerticalAlignment="Center"/>
                </StackPanel>
                <TextBlock x:Name="StatusText" VerticalAlignment="Center" Opacity="0.75"/>
            </StackPanel>
        </DockPanel>
```

With:

```xml
        <DockPanel Grid.Row="3" Margin="0,12,0,0">
            <StackPanel Orientation="Horizontal" DockPanel.Dock="Left">
                <Button Style="{StaticResource AccentButton}" Click="Uninstall_Click" Padding="14,6" AutomationProperties.Name="Uninstall">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE74D;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Uninstall"/>
                    </StackPanel>
                </Button>
                <Button Click="Scan_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Scan leftovers">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE721;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Scan leftovers"/>
                    </StackPanel>
                </Button>
                <Button Click="ScanByName_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Find leftovers by name"
                        ToolTip="Type the name of an already removed program in the Search box, then click here">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE71E;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Find leftovers by name"/>
                    </StackPanel>
                </Button>
                <Button Click="Update_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Update"
                        ToolTip="Update the selected program with winget (Windows Package Manager)">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE896;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Update"/>
                    </StackPanel>
                </Button>
                <Button Click="Junk_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Installer junk"
                        ToolTip="Find cached installers that no program uses any more">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE7B8;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Installer junk"/>
                    </StackPanel>
                </Button>
                <Button Click="Refresh_Click" Padding="9,6" Margin="8,0,0,0" ToolTip="Refresh the list"
                        AutomationProperties.Name="Refresh">
                    <TextBlock Style="{StaticResource IconText}" Text="&#xE72C;"/>
                </Button>
            </StackPanel>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Center">
                <StackPanel x:Name="BusyPanel" Orientation="Horizontal" Visibility="Collapsed" Margin="0,0,16,0">
                    <ProgressBar x:Name="BusyBar" Width="150" Height="4" MinHeight="4" Minimum="0" Maximum="100"
                                 VerticalAlignment="Center"/>
                    <TextBlock x:Name="BusyText" Margin="12,0,0,0" Style="{StaticResource SecondaryText}"/>
                </StackPanel>
                <TextBlock x:Name="StatusText" Style="{StaticResource SecondaryText}"/>
            </StackPanel>
        </DockPanel>
```

---

## ScanResultsWindow.xaml  (Priority 1)

### ScanResultsWindow.xaml – change 1: Page margin 20; page header uses the shared title/subtitle styles (texts still come from code-behind)

Replace:

```xml
    <Grid Margin="20,16,20,12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <StackPanel Grid.Row="0" Margin="0,0,0,10">
            <TextBlock x:Name="TitleText" FontSize="22" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
            <TextBlock x:Name="SummaryText" Opacity="0.75" Margin="0,4,0,0" TextWrapping="Wrap"/>
        </StackPanel>
```

With:

```xml
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <StackPanel Grid.Row="0" Margin="0,0,0,16">
            <TextBlock x:Name="TitleText" Style="{StaticResource PageTitle}"/>
            <TextBlock x:Name="SummaryText" Style="{StaticResource PageSubtitle}"/>
        </StackPanel>
```

### ScanResultsWindow.xaml – change 2: Red warning box becomes a warning InfoBar (icon, title, message). x:Name WarningBox / WarningText kept

Replace:

```xml
        <Border x:Name="WarningBox" Grid.Row="1" Background="#B91C1C" CornerRadius="6"
                Padding="12,8" Margin="0,0,0,10" Visibility="Collapsed">
            <TextBlock x:Name="WarningText" Foreground="White" FontWeight="SemiBold" TextWrapping="Wrap"/>
        </Border>
```

With:

```xml
        <Border x:Name="WarningBox" Grid.Row="1" Style="{StaticResource InfoBarWarning}"
                Margin="0,0,0,12" Visibility="Collapsed">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                <TextBlock Style="{StaticResource InfoBarIconWarning}"/>
                <StackPanel Grid.Column="1">
                    <TextBlock Style="{StaticResource InfoBarTitle}" Text="Check before you remove"/>
                    <TextBlock x:Name="WarningText" Style="{StaticResource InfoBarMessage}"/>
                </StackPanel>
            </Grid>
        </Border>
```

### ScanResultsWindow.xaml – change 3: List inside a card; DataGrid tuned inline; cell style based on the shared wrapping cell style

Replace:

```xml
        <DataGrid x:Name="ItemsGrid" Grid.Row="2" AutoGenerateColumns="False" CanUserAddRows="False"
                  CanUserDeleteRows="False" HeadersVisibility="Column" GridLinesVisibility="None"
                  SelectionMode="Single">
            <DataGrid.Resources>
                <Style x:Key="CellText" TargetType="TextBlock">
                    <Setter Property="TextWrapping" Value="Wrap"/>
                    <Setter Property="Margin" Value="0,4"/>
                    <Style.Triggers>
```

With:

```xml
        <Border Grid.Row="2" Style="{StaticResource ListCard}">
        <DataGrid x:Name="ItemsGrid" AutoGenerateColumns="False" CanUserAddRows="False"
                  CanUserDeleteRows="False" HeadersVisibility="Column" GridLinesVisibility="None"
                  SelectionMode="Single"
                  Background="Transparent" BorderThickness="0" MinRowHeight="36"
                  ColumnHeaderStyle="{StaticResource GridHeader}">
            <DataGrid.Resources>
                <Style x:Key="CellText" TargetType="TextBlock" BasedOn="{StaticResource GridWrapText}">
                    <Style.Triggers>
```

### ScanResultsWindow.xaml – change 4: Close the card Border after the DataGrid; command row with icon buttons and the accent primary action

Replace:

```xml
            </DataGrid.Columns>
        </DataGrid>

        <DockPanel Grid.Row="3" Margin="0,12,0,0">
            <StackPanel Orientation="Horizontal" DockPanel.Dock="Left">
                <Button Content="Select recommended" Click="Recommended_Click" Padding="14,6"/>
                <Button Content="Select none" Click="None_Click" Padding="14,6" Margin="8,0,0,0"/>
            </StackPanel>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                <Button x:Name="RemoveButton" Content="Remove selected" Click="Remove_Click" Padding="18,6"
                        FontWeight="SemiBold"/>
                <Button Content="Close" Click="Close_Click" Padding="18,6" Margin="8,0,0,0"/>
            </StackPanel>
        </DockPanel>
```

With:

```xml
            </DataGrid.Columns>
        </DataGrid>
        </Border>

        <DockPanel Grid.Row="3" Margin="0,12,0,0">
            <StackPanel Orientation="Horizontal" DockPanel.Dock="Left">
                <Button Click="Recommended_Click" Padding="12,6" AutomationProperties.Name="Select recommended">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE8FB;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Select recommended"/>
                    </StackPanel>
                </Button>
                <Button Click="None_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Select none">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE8E6;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Select none"/>
                    </StackPanel>
                </Button>
            </StackPanel>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                <!-- Text only: code-behind overwrites Content with "Remove selected (n)" -->
                <Button x:Name="RemoveButton" Style="{StaticResource AccentButton}" Content="Remove selected"
                        Click="Remove_Click" Padding="16,6"/>
                <Button Content="Close" Click="Close_Click" Padding="16,6" Margin="8,0,0,0" MinWidth="96"/>
            </StackPanel>
        </DockPanel>
```

### ScanResultsWindow.xaml – change 5: Busy overlay: dark scrim with a centred rounded card (all x:Names kept)

Replace:

```xml
        <Border x:Name="BusyOverlay" Grid.RowSpan="4" Margin="-20,-16,-20,-12" Background="#D9101014"
                Visibility="Collapsed">
            <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" Width="520">
                <TextBlock x:Name="BusyTitle" Text="Removing…" Foreground="White" FontSize="24" FontWeight="SemiBold"
                           HorizontalAlignment="Center"/>
                <TextBlock Text="Everything is saved to an Undo package first." Foreground="White" Opacity="0.7"
                           HorizontalAlignment="Center" Margin="0,4,0,18"/>
                <ProgressBar x:Name="BusyBar" Height="8" Minimum="0" Maximum="1" IsIndeterminate="True"/>
                <DockPanel Margin="0,10,0,0">
                    <TextBlock x:Name="BusyCount" DockPanel.Dock="Right" Foreground="White" Opacity="0.8"/>
                    <TextBlock x:Name="BusyItem" Foreground="White" Opacity="0.8" TextTrimming="CharacterEllipsis"
                               Margin="0,0,12,0"/>
                </DockPanel>
            </StackPanel>
        </Border>
```

With:

```xml
        <Border x:Name="BusyOverlay" Grid.RowSpan="4" Margin="-20" Style="{StaticResource BusyScrim}"
                Visibility="Collapsed">
            <Border Style="{StaticResource BusyCard}" Width="520">
                <StackPanel>
                    <TextBlock x:Name="BusyTitle" Text="Removing…" Style="{StaticResource BusyTitleText}"/>
                    <TextBlock Text="Everything is saved to an Undo package first." Style="{StaticResource BusyDetailText}"
                               HorizontalAlignment="Center" Margin="0,4,0,16"/>
                    <ProgressBar x:Name="BusyBar" Height="4" MinHeight="4" Minimum="0" Maximum="1" IsIndeterminate="True"/>
                    <DockPanel Margin="0,12,0,0">
                        <TextBlock x:Name="BusyCount" DockPanel.Dock="Right" Style="{StaticResource BusyDetailText}"/>
                        <TextBlock x:Name="BusyItem" Style="{StaticResource BusyDetailText}" Margin="0,0,12,0"/>
                    </DockPanel>
                </StackPanel>
            </Border>
        </Border>
```

---

## StartupWindow.xaml  (Priority 2)

### StartupWindow.xaml – change 1: Page margin 20; header without emoji; boot summary shown as an info InfoBar that hides itself while empty

Replace:

```xml
    <Grid Margin="20,16,20,12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <StackPanel Margin="0,0,0,10">
            <TextBlock Text="⚡ Startup" FontSize="22" FontWeight="SemiBold"/>
            <TextBlock Opacity="0.75" Margin="0,4,0,0" TextWrapping="Wrap"
                       Text="Everything that starts with Windows, grouped by program. Turning something off never deletes it – you can turn it back on at any time."/>
            <TextBlock x:Name="BootText" Margin="0,8,0,0" FontWeight="SemiBold" TextWrapping="Wrap"/>
        </StackPanel>
```

With:

```xml
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <StackPanel Margin="0,0,0,16">
            <TextBlock Text="Startup" Style="{StaticResource PageTitle}"/>
            <TextBlock Style="{StaticResource PageSubtitle}"
                       Text="Everything that starts with Windows, grouped by program. Turning something off never deletes it – you can turn it back on at any time."/>
            <Border Margin="0,12,0,0">
                <Border.Style>
                    <Style TargetType="Border" BasedOn="{StaticResource InfoBarInfo}">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding Text, ElementName=BootText}" Value="">
                                <Setter Property="Visibility" Value="Collapsed"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </Border.Style>
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Style="{StaticResource InfoBarIconInfo}"/>
                    <TextBlock x:Name="BootText" Grid.Column="1" Style="{StaticResource InfoBarMessage}" Margin="0"/>
                </Grid>
            </Border>
        </StackPanel>
```

### StartupWindow.xaml – change 2: Toolbar: search placeholder; Optimize is the accent button; icons instead of emoji

Replace:

```xml
        <DockPanel Grid.Row="1" Margin="0,0,0,10">
            <TextBlock Text="Search" VerticalAlignment="Center" Margin="0,0,10,0"/>
            <TextBox x:Name="SearchBox" Width="280" TextChanged="Filter_Changed"/>
            <CheckBox x:Name="ShowOff" Content="Show items that are off" IsChecked="True" Margin="18,0,0,0"
                      VerticalAlignment="Center" Checked="Filter_Changed" Unchecked="Filter_Changed"/>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                <Button x:Name="UndoOptimizeButton" Content="↶  Undo optimize" Click="UndoOptimize_Click" Padding="14,6" Margin="0,0,8,0"/>
                <Button Content="✨  Optimize" Click="Optimize_Click" Padding="14,6" FontWeight="SemiBold"
                        ToolTip="Turn off everything that is safe to turn off – in one click, with undo"/>
            </StackPanel>
        </DockPanel>
```

With:

```xml
        <DockPanel Grid.Row="1" Margin="0,0,0,12">
            <Grid Width="280">
                <TextBox x:Name="SearchBox" TextChanged="Filter_Changed" AutomationProperties.Name="Search startup items"/>
                <TextBlock Text="Search startup items" Margin="12,0,0,0" VerticalAlignment="Center" IsHitTestVisible="False"
                           Foreground="{DynamicResource TextFillColorSecondaryBrush}">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock">
                            <Setter Property="Visibility" Value="Collapsed"/>
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding Text.Length, ElementName=SearchBox}" Value="0">
                                    <Setter Property="Visibility" Value="Visible"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
            </Grid>
            <CheckBox x:Name="ShowOff" Content="Show items that are off" IsChecked="True" Margin="16,0,0,0"
                      VerticalAlignment="Center" Checked="Filter_Changed" Unchecked="Filter_Changed"/>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                <Button x:Name="UndoOptimizeButton" Click="UndoOptimize_Click" Padding="12,6" Margin="0,0,8,0"
                        AutomationProperties.Name="Undo optimize">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE7A7;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Undo optimize"/>
                    </StackPanel>
                </Button>
                <Button Style="{StaticResource AccentButton}" Click="Optimize_Click" Padding="14,6"
                        AutomationProperties.Name="Optimize"
                        ToolTip="Turn off everything that is safe to turn off – in one click, with undo">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xEC4A;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Optimize"/>
                    </StackPanel>
                </Button>
            </StackPanel>
        </DockPanel>
```

### StartupWindow.xaml – change 3: List inside a card; DataGrid tuned inline; cell style based on the shared wrapping cell style

Replace:

```xml
        <DataGrid x:Name="ItemsGrid" Grid.Row="2" AutoGenerateColumns="False" IsReadOnly="True"
                  SelectionMode="Extended" HeadersVisibility="Column" GridLinesVisibility="None"
                  CanUserSortColumns="False" VirtualizingPanel.IsVirtualizingWhenGrouping="True">
            <DataGrid.Resources>
                <Style x:Key="Cell" TargetType="TextBlock">
                    <Setter Property="TextWrapping" Value="Wrap"/>
                    <Setter Property="Margin" Value="0,3"/>
                    <Setter Property="ToolTip" Value="{Binding Tooltip}"/>
```

With:

```xml
        <Border Grid.Row="2" Style="{StaticResource ListCard}">
        <DataGrid x:Name="ItemsGrid" AutoGenerateColumns="False" IsReadOnly="True"
                  SelectionMode="Extended" HeadersVisibility="Column" GridLinesVisibility="None"
                  CanUserSortColumns="False" VirtualizingPanel.IsVirtualizingWhenGrouping="True"
                  Background="Transparent" BorderThickness="0" MinRowHeight="36"
                  ColumnHeaderStyle="{StaticResource GridHeader}">
            <DataGrid.Resources>
                <Style x:Key="Cell" TargetType="TextBlock" BasedOn="{StaticResource GridWrapText}">
                    <Setter Property="ToolTip" Value="{Binding Tooltip}"/>
```

### StartupWindow.xaml – change 4: Danger colour from the palette

Replace:

```xml
                            <Setter Property="Foreground" Value="#DC2626"/>
```

With:

```xml
                            <Setter Property="Foreground" Value="{StaticResource DangerBrush}"/>
```

### StartupWindow.xaml – change 5: Group header: theme-aware secondary colour instead of Gray

Replace:

```xml
                            <TextBlock FontSize="14" FontWeight="SemiBold" Margin="4,12,0,4">
                                <Run Text="{Binding Name, Mode=OneWay}"/>
                                <Run Text="  ·  " Foreground="Gray"/>
                                <Run Text="{Binding ItemCount, Mode=OneWay}" Foreground="Gray"/>
                                <Run Text=" item(s)" Foreground="Gray"/>
                            </TextBlock>
```

With:

```xml
                            <TextBlock Style="{StaticResource GroupHeaderText}">
                                <Run Text="{Binding Name, Mode=OneWay}"/>
                                <Run Text="  ·  " Foreground="{DynamicResource TextFillColorSecondaryBrush}"/>
                                <Run Text="{Binding ItemCount, Mode=OneWay}" Foreground="{DynamicResource TextFillColorSecondaryBrush}"/>
                                <Run Text=" item(s)" Foreground="{DynamicResource TextFillColorSecondaryBrush}"/>
                            </TextBlock>
```

### StartupWindow.xaml – change 6: Close the card; command row with Fluent icons; count as secondary text

Replace:

```xml
            </DataGrid.Columns>
        </DataGrid>

        <DockPanel Grid.Row="3" Margin="0,12,0,0">
            <StackPanel Orientation="Horizontal">
                <Button Content="○  Turn off" Click="Disable_Click" Padding="16,6" FontWeight="SemiBold"/>
                <Button Content="●  Turn on" Click="Enable_Click" Padding="16,6" Margin="8,0,0,0"/>
                <Button Content="⏱  Start 2 min later" Click="Delay_Click" Padding="16,6" Margin="8,0,0,0"
                        ToolTip="Keeps the program, but starts it 2 minutes after you sign in, so Windows is ready faster"/>
                <Button Content="🔒  Keep off" Click="Keep_Click" Padding="16,6" Margin="8,0,0,0"
                        ToolTip="Turn off and stay off – if the program switches itself back on, Purgely turns it off again"/>
                <Button Content="📂  Open location" Click="Open_Click" Padding="16,6" Margin="8,0,0,0"/>
            </StackPanel>
            <TextBlock x:Name="CountText" VerticalAlignment="Center" HorizontalAlignment="Right" Opacity="0.75"/>
        </DockPanel>
```

With:

```xml
            </DataGrid.Columns>
        </DataGrid>
        </Border>

        <DockPanel Grid.Row="3" Margin="0,12,0,0">
            <StackPanel Orientation="Horizontal">
                <Button Click="Disable_Click" Padding="12,6" AutomationProperties.Name="Turn off">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE7E8;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Turn off"/>
                    </StackPanel>
                </Button>
                <Button Click="Enable_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Turn on">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE768;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Turn on"/>
                    </StackPanel>
                </Button>
                <Button Click="Delay_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Start 2 min later"
                        ToolTip="Keeps the program, but starts it 2 minutes after you sign in, so Windows is ready faster">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE916;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Start 2 min later"/>
                    </StackPanel>
                </Button>
                <Button Click="Keep_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Keep off"
                        ToolTip="Turn off and stay off – if the program switches itself back on, Purgely turns it off again">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE72E;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Keep off"/>
                    </StackPanel>
                </Button>
                <Button Click="Open_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Open location">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE838;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Open location"/>
                    </StackPanel>
                </Button>
            </StackPanel>
            <TextBlock x:Name="CountText" HorizontalAlignment="Right" Style="{StaticResource SecondaryText}"/>
        </DockPanel>
```

### StartupWindow.xaml – change 7: Busy overlay: dark scrim with a centred rounded card

Replace:

```xml
        <Border x:Name="BusyOverlay" Grid.RowSpan="4" Margin="-20,-16,-20,-12" Background="#D9101014" Visibility="Collapsed">
            <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" Width="420">
                <TextBlock x:Name="BusyTitle" Text="Reading startup items…" Foreground="White" FontSize="20"
                           FontWeight="SemiBold" HorizontalAlignment="Center" Margin="0,0,0,14"/>
                <ProgressBar IsIndeterminate="True" Height="6"/>
            </StackPanel>
        </Border>
```

With:

```xml
        <Border x:Name="BusyOverlay" Grid.RowSpan="4" Margin="-20" Style="{StaticResource BusyScrim}" Visibility="Collapsed">
            <Border Style="{StaticResource BusyCard}" Width="420">
                <StackPanel>
                    <TextBlock x:Name="BusyTitle" Text="Reading startup items…" Style="{StaticResource BusyTitleText}"
                               Margin="0,0,0,16"/>
                    <ProgressBar IsIndeterminate="True" Height="4" MinHeight="4"/>
                </StackPanel>
            </Border>
        </Border>
```

---

## TrialWindow.xaml  (Phase 2 (optional tonight))

### TrialWindow.xaml – change 1: Page margin 20; header without emoji

Replace:

```xml
    <Grid Margin="20,16,20,12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <StackPanel Margin="0,0,0,12">
            <TextBlock Text="🧪 Trial Mode" FontSize="22" FontWeight="SemiBold"/>
            <TextBlock Opacity="0.75" Margin="0,4,0,0" TextWrapping="Wrap"
```

With:

```xml
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <StackPanel Margin="0,0,0,16">
            <TextBlock Text="Trial Mode" Style="{StaticResource PageTitle}"/>
            <TextBlock Style="{StaticResource PageSubtitle}"
```

### TrialWindow.xaml – change 2: Start panel becomes a Card; start button is the accent action

Replace:

```xml
        <Border Grid.Row="1" CornerRadius="8" Padding="16" Margin="0,0,0,12" BorderThickness="1"
                BorderBrush="#40808080">
            <Grid>
                <StackPanel x:Name="StartPanel">
                    <CheckBox x:Name="RestorePointBox" IsChecked="True"
                              Content="Also create a system restore point first (recommended, extra safety net)"/>
                    <Button Content="Choose installer and start…" Click="Start_Click" Padding="18,8"
                            FontWeight="SemiBold" HorizontalAlignment="Left" Margin="0,12,0,0"/>
                </StackPanel>
```

With:

```xml
        <Border Grid.Row="1" Style="{StaticResource Card}" Margin="0,0,0,12">
            <Grid>
                <StackPanel x:Name="StartPanel">
                    <TextBlock Style="{StaticResource BodyStrongText}" Text="Start a new trial" Margin="0,0,0,8"/>
                    <CheckBox x:Name="RestorePointBox" IsChecked="True"
                              Content="Also create a system restore point first (recommended, extra safety net)"/>
                    <Button Style="{StaticResource AccentButton}" Click="Start_Click" Padding="14,6"
                            HorizontalAlignment="Left" Margin="0,12,0,0" AutomationProperties.Name="Choose installer and start">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock Style="{StaticResource IconText}" Text="&#xE768;"/>
                            <TextBlock Style="{StaticResource ButtonLabel}" Text="Choose installer and start…"/>
                        </StackPanel>
                    </Button>
                </StackPanel>
```

### TrialWindow.xaml – change 3: Busy and installing states: secondary text styles; finish button is the accent action

Replace:

```xml
                        <TextBlock x:Name="ElapsedText" DockPanel.Dock="Right" Opacity="0.7" Margin="12,0,0,0"/>
                        <TextBlock x:Name="BusyText" FontWeight="SemiBold" TextWrapping="Wrap"/>
                    </DockPanel>
                    <ProgressBar IsIndeterminate="True" Height="6"/>
                    <TextBlock Opacity="0.7" Margin="0,8,0,0" TextWrapping="Wrap"
                               Text="Please don't install or remove anything else while Purgely is working."/>
                </StackPanel>

                <StackPanel x:Name="InstallingPanel" Visibility="Collapsed">
                    <TextBlock FontWeight="SemiBold" FontSize="15" Text="The installer is running – install the program as usual."/>
                    <TextBlock Opacity="0.75" Margin="0,4,0,0" TextWrapping="Wrap"
                               Text="When the installer has completely FINISHED, click the button below. (If the installer restarts the PC, open Trial Mode again afterwards and click Analyze.)"/>
                    <Button Content="Installation finished – analyze" Click="Finished_Click" Padding="18,8"
                            FontWeight="SemiBold" HorizontalAlignment="Left" Margin="0,12,0,0"/>
                </StackPanel>
```

With:

```xml
                        <TextBlock x:Name="ElapsedText" DockPanel.Dock="Right" Style="{StaticResource SecondaryText}" Margin="12,0,0,0"/>
                        <TextBlock x:Name="BusyText" Style="{StaticResource BodyStrongText}"/>
                    </DockPanel>
                    <ProgressBar IsIndeterminate="True" Height="4" MinHeight="4"/>
                    <TextBlock Style="{StaticResource SecondaryText}" Margin="0,8,0,0" TextWrapping="Wrap"
                               Text="Please don't install or remove anything else while Purgely is working."/>
                </StackPanel>

                <StackPanel x:Name="InstallingPanel" Visibility="Collapsed">
                    <TextBlock Style="{StaticResource BodyStrongText}" Text="The installer is running – install the program as usual."/>
                    <TextBlock Style="{StaticResource PageSubtitle}"
                               Text="When the installer has completely finished, click the button below. (If the installer restarts the PC, open Trial Mode again afterwards and click Analyze.)"/>
                    <Button Style="{StaticResource AccentButton}" Click="Finished_Click" Padding="14,6"
                            HorizontalAlignment="Left" Margin="0,12,0,0" AutomationProperties.Name="Installation finished, analyze">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock Style="{StaticResource IconText}" Text="&#xE73E;"/>
                            <TextBlock Style="{StaticResource ButtonLabel}" Text="Installation finished – analyze"/>
                        </StackPanel>
                    </Button>
                </StackPanel>
```

### TrialWindow.xaml – change 4: Trials list inside a card; command row with icons

Replace:

```xml
        <DataGrid x:Name="TrialsGrid" Grid.Row="2" AutoGenerateColumns="False" IsReadOnly="True"
                  SelectionMode="Single" HeadersVisibility="Column" GridLinesVisibility="None">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Started" Binding="{Binding StartedText}" Width="140"/>
                <DataGridTextColumn Header="Program" Binding="{Binding Title}" Width="2*"/>
                <DataGridTextColumn Header="Status" Binding="{Binding StateText}" Width="*"/>
                <DataGridTextColumn Header="Changes recorded" Binding="{Binding ChangesText}" Width="2*"/>
            </DataGrid.Columns>
        </DataGrid>

        <DockPanel Grid.Row="3" Margin="0,12,0,0">
            <StackPanel Orientation="Horizontal">
                <Button Content="↶  Revert" Click="Revert_Click" Padding="18,6" FontWeight="SemiBold"/>
                <Button Content="✔  Keep" Click="Keep_Click" Padding="18,6" Margin="8,0,0,0"/>
                <Button Content="Analyze" Click="Analyze_Click" Padding="18,6" Margin="8,0,0,0"
                        ToolTip="For a trial whose installer has finished (e.g. after a restart)"/>
                <Button Content="Details" Click="Details_Click" Padding="18,6" Margin="8,0,0,0"/>
                <Button Content="Delete record" Click="Delete_Click" Padding="18,6" Margin="8,0,0,0"/>
            </StackPanel>
            <Button Content="Close" Click="Close_Click" Padding="18,6" HorizontalAlignment="Right"/>
        </DockPanel>
```

With:

```xml
        <Border Grid.Row="2" Style="{StaticResource ListCard}">
        <DataGrid x:Name="TrialsGrid" AutoGenerateColumns="False" IsReadOnly="True"
                  SelectionMode="Single" HeadersVisibility="Column" GridLinesVisibility="None"
                  Background="Transparent" BorderThickness="0" MinRowHeight="36"
                  ColumnHeaderStyle="{StaticResource GridHeader}">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Started" Binding="{Binding StartedText}" Width="140" ElementStyle="{StaticResource GridText}"/>
                <DataGridTextColumn Header="Program" Binding="{Binding Title}" Width="2*" ElementStyle="{StaticResource GridText}"/>
                <DataGridTextColumn Header="Status" Binding="{Binding StateText}" Width="*" ElementStyle="{StaticResource GridText}"/>
                <DataGridTextColumn Header="Changes recorded" Binding="{Binding ChangesText}" Width="2*" ElementStyle="{StaticResource GridText}"/>
            </DataGrid.Columns>
        </DataGrid>
        </Border>

        <DockPanel Grid.Row="3" Margin="0,12,0,0">
            <StackPanel Orientation="Horizontal">
                <Button Click="Revert_Click" Padding="12,6" AutomationProperties.Name="Revert">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE7A7;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Revert"/>
                    </StackPanel>
                </Button>
                <Button Click="Keep_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Keep">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE73E;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Keep"/>
                    </StackPanel>
                </Button>
                <Button Click="Analyze_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Analyze"
                        ToolTip="For a trial whose installer has finished (e.g. after a restart)">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE9D9;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Analyze"/>
                    </StackPanel>
                </Button>
                <Button Click="Details_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Details">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE946;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Details"/>
                    </StackPanel>
                </Button>
                <Button Click="Delete_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Delete record">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE74D;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Delete record"/>
                    </StackPanel>
                </Button>
            </StackPanel>
            <Button Content="Close" Click="Close_Click" Padding="16,6" MinWidth="96" HorizontalAlignment="Right"/>
        </DockPanel>
```

---

## TimelineWindow.xaml  (Phase 2 (optional tonight))

### TimelineWindow.xaml – change 1: Page margin 20; shared header styles; search placeholder

Replace:

```xml
    <Grid Margin="20,16,20,12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <StackPanel Margin="0,0,0,10">
            <TextBlock Text="PC Timeline" FontSize="22" FontWeight="SemiBold"/>
            <TextBlock Opacity="0.75" Margin="0,4,0,0" TextWrapping="Wrap"
                       Text="Everything installed, updated and removed on this PC – and everything Purgely did. ⚠ marks programs that were installed together by different publishers (possible bundleware)."/>
        </StackPanel>

        <DockPanel Grid.Row="1" Margin="0,0,0,10">
            <TextBlock Text="Search" VerticalAlignment="Center" Margin="0,0,10,0"/>
            <TextBox x:Name="SearchBox" Width="300" TextChanged="Filter_Changed"/>
```

With:

```xml
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <StackPanel Margin="0,0,0,16">
            <TextBlock Text="PC Timeline" Style="{StaticResource PageTitle}"/>
            <TextBlock Style="{StaticResource PageSubtitle}"
                       Text="Everything installed, updated and removed on this PC – and everything Purgely did. ⚠ marks programs that were installed together by different publishers (possible bundleware)."/>
        </StackPanel>

        <DockPanel Grid.Row="1" Margin="0,0,0,12">
            <Grid Width="300">
                <TextBox x:Name="SearchBox" TextChanged="Filter_Changed" AutomationProperties.Name="Search events"/>
                <TextBlock Text="Search events" Margin="12,0,0,0" VerticalAlignment="Center" IsHitTestVisible="False"
                           Foreground="{DynamicResource TextFillColorSecondaryBrush}">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock">
                            <Setter Property="Visibility" Value="Collapsed"/>
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding Text.Length, ElementName=SearchBox}" Value="0">
                                    <Setter Property="Visibility" Value="Visible"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
            </Grid>
```

### TimelineWindow.xaml – change 2: Events list inside a card; shared cell and group header styles

Replace:

```xml
        <DataGrid x:Name="EventsGrid" Grid.Row="2" AutoGenerateColumns="False" IsReadOnly="True"
                  HeadersVisibility="Column" GridLinesVisibility="None" CanUserSortColumns="False"
                  VirtualizingPanel.IsVirtualizingWhenGrouping="True">
            <DataGrid.Resources>
                <Style x:Key="EventText" TargetType="TextBlock">
                    <Setter Property="TextWrapping" Value="Wrap"/>
                    <Setter Property="Margin" Value="0,3"/>
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding IsWarning}" Value="True">
                            <Setter Property="Foreground" Value="#D97706"/>
```

With:

```xml
        <Border Grid.Row="2" Style="{StaticResource ListCard}">
        <DataGrid x:Name="EventsGrid" AutoGenerateColumns="False" IsReadOnly="True"
                  HeadersVisibility="Column" GridLinesVisibility="None" CanUserSortColumns="False"
                  VirtualizingPanel.IsVirtualizingWhenGrouping="True"
                  Background="Transparent" BorderThickness="0" MinRowHeight="36"
                  ColumnHeaderStyle="{StaticResource GridHeader}">
            <DataGrid.Resources>
                <Style x:Key="EventText" TargetType="TextBlock" BasedOn="{StaticResource GridWrapText}">
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding IsWarning}" Value="True">
                            <Setter Property="Foreground" Value="{StaticResource AttentionBrush}"/>
```

### TimelineWindow.xaml – change 3: Group header uses the shared style

Replace:

```xml
                            <TextBlock Text="{Binding Name}" FontSize="15" FontWeight="SemiBold" Margin="4,14,0,4"/>
```

With:

```xml
                            <TextBlock Text="{Binding Name}" Style="{StaticResource GroupHeaderText}"/>
```

### TimelineWindow.xaml – change 4: Close the card; count as secondary text

Replace:

```xml
            </DataGrid.Columns>
        </DataGrid>

        <DockPanel Grid.Row="3" Margin="0,12,0,0">
            <TextBlock x:Name="CountText" VerticalAlignment="Center" Opacity="0.75"/>
            <Button Content="Close" Click="Close_Click" Padding="18,6" HorizontalAlignment="Right"/>
        </DockPanel>
```

With:

```xml
            </DataGrid.Columns>
        </DataGrid>
        </Border>

        <DockPanel Grid.Row="3" Margin="0,12,0,0">
            <TextBlock x:Name="CountText" Style="{StaticResource SecondaryText}"/>
            <Button Content="Close" Click="Close_Click" Padding="16,6" MinWidth="96" HorizontalAlignment="Right"/>
        </DockPanel>
```

---

## UndoWindow.xaml  (Phase 2 (optional tonight))

### UndoWindow.xaml – change 1: Whole body: header styles, list card, Restore as accent action with icons

Replace:

```xml
    <Grid Margin="20,16,20,12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <StackPanel Margin="0,0,0,10">
            <TextBlock Text="Undo center" FontSize="22" FontWeight="SemiBold"/>
            <TextBlock Opacity="0.75" Margin="0,4,0,0" TextWrapping="Wrap"
                       Text="Every cleanup is saved here. Restore puts everything back exactly where it was. Discard frees the disk space."/>
        </StackPanel>
        <DataGrid x:Name="PackagesGrid" Grid.Row="1" AutoGenerateColumns="False" IsReadOnly="True"
                  SelectionMode="Single" HeadersVisibility="Column" GridLinesVisibility="None">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Date" Binding="{Binding CreatedText}" Width="150"/>
                <DataGridTextColumn Header="Program" Binding="{Binding Title}" Width="2*"/>
                <DataGridTextColumn Header="Items" Binding="{Binding Count}" Width="70"/>
                <DataGridTextColumn Header="Status" Binding="{Binding Status}" Width="*"/>
            </DataGrid.Columns>
        </DataGrid>
        <DockPanel Grid.Row="2" Margin="0,12,0,0">
            <StackPanel Orientation="Horizontal">
                <Button Content="Restore" Click="Restore_Click" Padding="18,6" FontWeight="SemiBold"/>
                <Button Content="Discard" Click="Discard_Click" Padding="18,6" Margin="8,0,0,0"/>
                <Button Content="Open folder" Click="Open_Click" Padding="18,6" Margin="8,0,0,0"/>
            </StackPanel>
            <Button Content="Close" Click="Close_Click" Padding="18,6" HorizontalAlignment="Right"/>
        </DockPanel>
```

With:

```xml
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <StackPanel Margin="0,0,0,16">
            <TextBlock Text="Undo center" Style="{StaticResource PageTitle}"/>
            <TextBlock Style="{StaticResource PageSubtitle}"
                       Text="Every cleanup is saved here. Restore puts everything back exactly where it was. Discard frees the disk space."/>
        </StackPanel>
        <Border Grid.Row="1" Style="{StaticResource ListCard}">
        <DataGrid x:Name="PackagesGrid" AutoGenerateColumns="False" IsReadOnly="True"
                  SelectionMode="Single" HeadersVisibility="Column" GridLinesVisibility="None"
                  Background="Transparent" BorderThickness="0" MinRowHeight="36"
                  ColumnHeaderStyle="{StaticResource GridHeader}">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Date" Binding="{Binding CreatedText}" Width="150" ElementStyle="{StaticResource GridText}"/>
                <DataGridTextColumn Header="Program" Binding="{Binding Title}" Width="2*" ElementStyle="{StaticResource GridText}"/>
                <DataGridTextColumn Header="Items" Binding="{Binding Count}" Width="70" ElementStyle="{StaticResource GridText}"/>
                <DataGridTextColumn Header="Status" Binding="{Binding Status}" Width="*" ElementStyle="{StaticResource GridText}"/>
            </DataGrid.Columns>
        </DataGrid>
        </Border>
        <DockPanel Grid.Row="2" Margin="0,12,0,0">
            <StackPanel Orientation="Horizontal">
                <Button Style="{StaticResource AccentButton}" Click="Restore_Click" Padding="14,6" AutomationProperties.Name="Restore">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE777;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Restore"/>
                    </StackPanel>
                </Button>
                <Button Click="Discard_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Discard">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE74D;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Discard"/>
                    </StackPanel>
                </Button>
                <Button Click="Open_Click" Padding="12,6" Margin="8,0,0,0" AutomationProperties.Name="Open folder">
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Style="{StaticResource IconText}" Text="&#xE838;"/>
                        <TextBlock Style="{StaticResource ButtonLabel}" Text="Open folder"/>
                    </StackPanel>
                </Button>
            </StackPanel>
            <Button Content="Close" Click="Close_Click" Padding="16,6" MinWidth="96" HorizontalAlignment="Right"/>
        </DockPanel>
```

---

## RelatedWindow.xaml  (Phase 2 (optional tonight))

### RelatedWindow.xaml – change 1: Whole body: header styles, explanation as info InfoBar, list card, accent primary action

Replace:

```xml
    <Grid Margin="20,16,20,12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <StackPanel Margin="0,0,0,10">
            <TextBlock x:Name="TitleText" FontSize="20" FontWeight="SemiBold" TextWrapping="Wrap"/>
            <TextBlock Opacity="0.75" Margin="0,4,0,0" TextWrapping="Wrap"
                       Text="These programs may belong to the same suite. Only clear add-ons and parts of the same product are ticked – check the list before you continue. Some parts of a suite also work on their own: untick those if you still use them."/>
        </StackPanel>
        <DataGrid x:Name="RelatedGrid" Grid.Row="1" AutoGenerateColumns="False" CanUserAddRows="False"
                  HeadersVisibility="Column" GridLinesVisibility="None" SelectionMode="Single">
```

With:

```xml
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <StackPanel Margin="0,0,0,16">
            <TextBlock x:Name="TitleText" Style="{StaticResource PageTitle}" TextWrapping="Wrap"/>
            <Border Style="{StaticResource InfoBarInfo}" Margin="0,12,0,0">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Style="{StaticResource InfoBarIconInfo}"/>
                    <TextBlock Grid.Column="1" Style="{StaticResource InfoBarMessage}" Margin="0"
                               Text="These programs may belong to the same suite. Only clear add-ons and parts of the same product are ticked – check the list before you continue. Some parts of a suite also work on their own: untick those if you still use them."/>
                </Grid>
            </Border>
        </StackPanel>
        <Border Grid.Row="1" Style="{StaticResource ListCard}">
        <DataGrid x:Name="RelatedGrid" AutoGenerateColumns="False" CanUserAddRows="False"
                  HeadersVisibility="Column" GridLinesVisibility="None" SelectionMode="Single"
                  Background="Transparent" BorderThickness="0" MinRowHeight="36"
                  ColumnHeaderStyle="{StaticResource GridHeader}">
```

### RelatedWindow.xaml – change 2: Columns: shared cell styles; close the card; accent primary action

Replace:

```xml
                <DataGridTextColumn Header="Program" Binding="{Binding Name}" Width="2*" IsReadOnly="True"/>
                <DataGridTextColumn Header="Publisher" Binding="{Binding Publisher}" Width="*" IsReadOnly="True"/>
                <DataGridTextColumn Header="Part of the suite?" Binding="{Binding ScoreText}" Width="120" IsReadOnly="True"/>
                <DataGridTextColumn Header="Why" Binding="{Binding Why}" Width="2*" IsReadOnly="True">
                    <DataGridTextColumn.ElementStyle>
                        <Style TargetType="TextBlock"><Setter Property="TextWrapping" Value="Wrap"/></Style>
                    </DataGridTextColumn.ElementStyle>
                </DataGridTextColumn>
            </DataGrid.Columns>
        </DataGrid>
        <DockPanel Grid.Row="2" Margin="0,12,0,0">
            <Button Content="Uninstall selected" Click="Uninstall_Click" Padding="18,6" FontWeight="SemiBold"/>
            <Button Content="Not now" Click="Close_Click" Padding="18,6" HorizontalAlignment="Right"/>
        </DockPanel>
```

With:

```xml
                <DataGridTextColumn Header="Program" Binding="{Binding Name}" Width="2*" IsReadOnly="True" ElementStyle="{StaticResource GridText}"/>
                <DataGridTextColumn Header="Publisher" Binding="{Binding Publisher}" Width="*" IsReadOnly="True" ElementStyle="{StaticResource GridText}"/>
                <DataGridTextColumn Header="Part of the suite?" Binding="{Binding ScoreText}" Width="120" IsReadOnly="True" ElementStyle="{StaticResource GridText}"/>
                <DataGridTextColumn Header="Why" Binding="{Binding Why}" Width="2*" IsReadOnly="True" ElementStyle="{StaticResource GridWrapText}"/>
            </DataGrid.Columns>
        </DataGrid>
        </Border>
        <DockPanel Grid.Row="2" Margin="0,12,0,0">
            <Button Style="{StaticResource AccentButton}" Click="Uninstall_Click" Padding="14,6" AutomationProperties.Name="Uninstall selected">
                <StackPanel Orientation="Horizontal">
                    <TextBlock Style="{StaticResource IconText}" Text="&#xE74D;"/>
                    <TextBlock Style="{StaticResource ButtonLabel}" Text="Uninstall selected"/>
                </StackPanel>
            </Button>
            <Button Content="Not now" Click="Close_Click" Padding="16,6" MinWidth="96" HorizontalAlignment="Right"/>
        </DockPanel>
```

---

## After applying: 5-minute check on Windows

1. Build. Then run with Windows set to **Light**, then to **Dark** (Settings > Personalization > Colors). Text, cards, InfoBars and the accent button must be readable in both.
2. MainWindow: type in the search box; the placeholder disappears and filtering still works. Ctrl+F, Ctrl+V and Esc still work. Sort by Size and Installed; the chevron appears in the header. Resize a column by dragging the header edge. Double-click a row; it still starts Uninstall.
3. ScanResultsWindow: open one with a warning (scan leftovers of a program that is still installed); the orange InfoBar appears. Tick and untick items; "Remove selected (n)" updates and is disabled at 0 (grey, readable).
4. StartupWindow: the blue boot-time InfoBar appears once the scan has finished; the busy card shows while loading; group headers are readable in dark mode.
5. Keyboard: Tab through a window. Every button shows a focus ring and Enter/Space triggers it.

## Not covered here (needs small code-behind changes – later)

These are visible "old-looking" spots that cannot be fixed in XAML alone:

- **Emoji in strings set from code**: `MainWindow.xaml.cs` line 27 (`"✔ Administrator"` / `"⚠ Not running as administrator"` – now inside a pill badge, so drop the symbols), line 425 (`"⚠ This program is still INSTALLED"` – the InfoBar now has its own warning icon, so the ⚠ is doubled), `ScanResultsWindow.xaml.cs` lines 39 and 42 (🎉, 🧟), `StartupWindow.xaml.cs` line 91, `TimelineWindow.xaml.cs` line 46, `TrialWindow.xaml.cs` line 151, plus status texts in `Core/` (StartupModels.cs, LeftoverItem.cs, Timeline.cs, TrialEngine.cs).
- **MessageBox** (about 84 calls): the classic Win32 message box is the most "1990" element left. Replace it step by step with a small Fluent dialog window (title, message, accent primary button, secondary button) or, for non-blocking results, with an InfoBar in the window.
- **ALL-CAPS emphasis** in messages ("INSTALLED", "FINISHED"): Windows 11 never shouts; use a warning InfoBar or bold text instead.
- **Mica backdrop** is supplied by the Fluent theme on Windows 11; do not set a `Background` on any `Window`, or the backdrop is lost.
