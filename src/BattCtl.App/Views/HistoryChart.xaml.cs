using BattCtl.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace BattCtl.App.Views;

// Hover payload for the page readout: one sample, or the bracket of a gap
// when the pointer is too far from any sample to snap to one.
public sealed record HistoryHover(HistorySample? Sample, DateTimeOffset? GapStart, DateTimeOffset? GapEnd);

// Hand-drawn charge-level chart: an exact step-after line at low sample
// density, a per-pixel min/max envelope once samples exceed two per pixel.
// State is encoded by color AND dash pattern, and repeated along a 4px strip
// at the baseline, so charging / discharging / AC idle stay distinguishable
// without color vision and without hovering. The line, the area and the strip
// break wherever the sampler has no data (a gap over 10 minutes: sleep, off,
// or the task not yet installed).
public sealed partial class HistoryChart : UserControl
{
    private const double GutterLeft = 36;
    private const double MarginRight = 8;
    private const double PlotTop = 6;
    private const double PlotHeight = 136;
    private static readonly TimeSpan GapThreshold = TimeSpan.FromMinutes(10);

    private Brush _gridBrush = null!;
    private Brush _chargingBrush = null!;
    private Brush _dischargingBrush = null!;
    private Brush _idleBrush = null!;
    private Brush _primaryBrush = null!;
    private Brush _cardBrush = null!;
    private Brush _limitBrush = null!;

    private IReadOnlyList<HistorySample> _samples = Array.Empty<HistorySample>();
    private HistoryRange _range = HistoryRange.Day;
    private DateTimeOffset _from;
    private DateTimeOffset _to;
    private bool _showLimitGuide;

    public event EventHandler<HistoryHover?>? HoverChanged;

    public HistoryChart()
    {
        InitializeComponent();
        ResolveBrushes();

        Loaded += (_, _) => Render();
        SizeChanged += (_, _) => Render();
        // Fallback brushes are theme-picked; re-resolve so a theme change
        // never leaves a stale fallback on screen.
        ActualThemeChanged += (_, _) =>
        {
            ResolveBrushes();
            Render();
        };
        PlotCanvas.PointerMoved += PlotCanvas_PointerMoved;
        PlotCanvas.PointerExited += (_, _) => ClearHover();
    }

    public void SetData(
        IReadOnlyList<HistorySample> samples,
        HistoryRange range,
        DateTimeOffset from,
        DateTimeOffset to,
        bool showLimitGuide)
    {
        _samples = samples;
        _range = range;
        _from = from;
        _to = to;
        _showLimitGuide = showLimitGuide;
        ClearHover();
        Render();
    }

    private void ResolveBrushes()
    {
        _gridBrush = Resolve("DividerStrokeColorDefaultBrush", "#2B2B2B", "#E5E5E5");
        _chargingBrush = Resolve("SystemFillColorSuccessBrush", "#6CCB5F", "#0F7B0F");
        _dischargingBrush = Resolve("TextFillColorSecondaryBrush", "#C5C5C5", "#5A5A5A");
        _idleBrush = Resolve("ControlStrongStrokeColorDefaultBrush", "#8B8B8B", "#6B6B6B");
        _primaryBrush = Resolve("TextFillColorPrimaryBrush", "#FFFFFF", "#1A1A1A");
        _cardBrush = Resolve("CardBackgroundFillColorDefaultBrush", "#2C2C2C", "#FFFFFF");
        _limitBrush = Resolve("HistoryLimitLineBrush", "#8A8AF0", "#3A47B8");
    }

    private static Brush Resolve(string key, string darkHex, string lightHex)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }

        var hex = Application.Current.RequestedTheme == ApplicationTheme.Dark ? darkHex : lightHex;
        var color = Windows.UI.Color.FromArgb(
            0xFF,
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));
        return new SolidColorBrush(color);
    }

    // ---------------------------------------------------------------- render

    private void Render()
    {
        PlotCanvas.Children.Clear();
        HoverCanvas.Children.Clear();
        LabelCanvas.Children.Clear();

        var width = PlotCanvas.ActualWidth;
        if (width <= 0 || _samples.Count == 0 || _to <= _from)
        {
            return;
        }

        var plotLeft = GutterLeft;
        var plotRight = Math.Max(plotLeft + 10, width - MarginRight);

        DrawYAxis(plotLeft, plotRight);
        DrawXLabels();

        // Downsample to at most ~2 samples per horizontal pixel: below that
        // the exact step-after shape is kept, above it each pixel column
        // contributes its min and max so a dip can never hide in a pixel.
        if (_samples.Count <= 2 * (plotRight - plotLeft))
        {
            RenderDense();
        }
        else
        {
            RenderColumns(plotLeft, plotRight);
        }
    }

    private void DrawYAxis(double plotLeft, double plotRight)
    {
        foreach (var level in new[] { 0, 25, 50, 75, 100 })
        {
            var y = Snap(YOf(level));

            PlotCanvas.Children.Add(new Line
            {
                X1 = plotLeft,
                X2 = plotRight,
                Y1 = y,
                Y2 = y,
                StrokeThickness = 1,
                Stroke = _gridBrush,
            });

            var label = new TextBlock
            {
                Text = level == 100 ? "100%" : level.ToString(),
                FontSize = 12,
                Foreground = _dischargingBrush,
                Width = GutterLeft - 4,
                TextAlignment = TextAlignment.Right,
            };
            Canvas.SetTop(label, y - 8);
            PlotCanvas.Children.Add(label);
        }
    }

    private void DrawXLabels()
    {
        var step = _range switch
        {
            HistoryRange.Day => TimeSpan.FromHours(6),
            HistoryRange.Week => TimeSpan.FromDays(1),
            HistoryRange.Month => TimeSpan.FromDays(5),
            _ => TimeSpan.FromDays(10),
        };

        // Anchored on local midnight; the 24h range anchors on 6-hour marks.
        var current = _from.LocalDateTime;
        var day = current.Date;
        var start = _range == HistoryRange.Day
            ? AlignUp(current, step)
            : day;
        if (_range != HistoryRange.Day && start < current && start != day)
        {
            start = start.AddDays(1);
        }

        for (var t = start; t <= _to.LocalDateTime; t = t.Add(step))
        {
            var local = new DateTimeOffset(t, TimeZoneInfo.Local.GetUtcOffset(t));
            if (local < _from || local > _to)
            {
                continue;
            }

            var text = _range switch
            {
                HistoryRange.Day => t.Hour == 0 ? t.ToString("ddd HH:mm") : t.ToString("HH:mm"),
                HistoryRange.Week => t.ToString("ddd"),
                _ => t.ToString("MMM d"),
            };

            var label = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = _dischargingBrush,
                Width = 72,
                TextAlignment = TextAlignment.Center,
            };
            Canvas.SetTop(label, 0);
            Canvas.SetLeft(label, Math.Clamp(XOf(local) - 36, 0, Math.Max(0, PlotCanvas.ActualWidth - 72)));
            LabelCanvas.Children.Add(label);
        }
    }

    private static DateTime AlignUp(DateTime value, TimeSpan step)
    {
        var remainder = value.Ticks % step.Ticks;
        return remainder == 0 ? value : new DateTime(value.Ticks - remainder + step.Ticks, value.Kind);
    }

    // ------------------------------------------------------------- dense path

    private void RenderDense()
    {
        Run strip = new(_samples.Count);
        Run line = new(_samples.Count);
        Run limit = new(_samples.Count);

        for (var i = 0; i < _samples.Count; i++)
        {
            var sample = _samples[i];
            if (sample.Timestamp is not DateTimeOffset timestamp)
            {
                strip.FlushStrip(this);
                line.FlushLine(this);
                limit.FlushLimit(this);
                continue;
            }

            var x = XOf(timestamp);
            var kind = HistoryAnalysis.DeriveKind(sample);

            // A gap before this sample closes every open run.
            if (i > 0 && _samples[i - 1].Timestamp is DateTimeOffset previousTimestamp
                && timestamp - previousTimestamp > GapThreshold)
            {
                strip.FlushStrip(this);
                line.FlushLine(this);
                limit.FlushLimit(this);
                AddGapTick(XOf(previousTimestamp));
                AddGapTick(x);
            }

            if (strip.Kind != kind)
            {
                strip.FlushStrip(this);
                strip.Kind = kind;
                strip.StartX = x;
            }

            strip.EndX = x;

            // The interval [i, i+1] is drawn in the state of sample i, so a
            // state change at i+1 cannot recolor the horizontal that led in.
            if (i + 1 < _samples.Count && _samples[i + 1].Timestamp is DateTimeOffset nextTimestamp)
            {
                if (nextTimestamp - timestamp > GapThreshold)
                {
                    line.FlushLine(this);
                }
                else
                {
                    var nextX = XOf(nextTimestamp);
                    var nextSample = _samples[i + 1];

                    if (line.Kind != kind || line.Points.Count == 0)
                    {
                        line.FlushLine(this);
                        line.Kind = kind;
                        line.StartX = x;
                        if (sample.Pct is int pct)
                        {
                            line.Points.Add(new Point(x, YOf(pct)));
                        }
                    }

                    if (sample.Pct is int fromPct && nextSample.Pct is int toPct)
                    {
                        line.Points.Add(new Point(nextX, YOf(fromPct)));
                        line.Points.Add(new Point(nextX, YOf(toPct)));
                        line.EndX = nextX;
                    }
                    else
                    {
                        // Unknown level at either end: the line breaks rather
                        // than invent a value. The strip keeps the state.
                        line.FlushLine(this);
                    }
                }
            }

            if (_showLimitGuide)
            {
                if (sample.LimitSet == true)
                {
                    if (limit.StartX < 0)
                    {
                        limit.StartX = x;
                    }

                    limit.EndX = x;
                }
                else
                {
                    limit.FlushLimit(this);
                }
            }
        }

        strip.FlushStrip(this);
        line.FlushLine(this);
        limit.FlushLimit(this);
    }

    // ----------------------------------------------------------- column path

    private void RenderColumns(double plotLeft, double plotRight)
    {
        var plotWidth = plotRight - plotLeft;
        var columnCount = Math.Max(1, (int)Math.Floor(plotWidth));
        var columns = new Column?[columnCount];
        var spanMs = (_to - _from).TotalMilliseconds;

        foreach (var sample in _samples)
        {
            if (sample.Timestamp is not DateTimeOffset timestamp)
            {
                continue;
            }

            var index = (int)Math.Floor((timestamp - _from).TotalMilliseconds / spanMs * columnCount);
            if (index < 0 || index >= columnCount)
            {
                continue;
            }

            var column = columns[index];
            if (column is null)
            {
                columns[index] = column = new Column
                {
                    FirstTimestamp = timestamp,
                    LastTimestamp = timestamp,
                    Kind = HistoryAnalysis.DeriveKind(sample),
                };
            }
            else
            {
                column.LastTimestamp = timestamp;
            }

            if (sample.Pct is int pct)
            {
                column.MinPct = Math.Min(column.MinPct ?? pct, pct);
                column.MaxPct = Math.Max(column.MaxPct ?? pct, pct);
            }

            column.HasLimit |= sample.LimitSet == true;
        }

        Column? previous = null;
        Run strip = new(columnCount);
        Run line = new(columnCount);
        Run limit = new(columnCount);

        for (var c = 0; c < columnCount; c++)
        {
            var column = columns[c];
            if (column is null)
            {
                continue;
            }

            var x = plotLeft + c;

            if (previous is not null && column.FirstTimestamp - previous.LastTimestamp > GapThreshold)
            {
                strip.FlushStrip(this);
                line.FlushLine(this);
                limit.FlushLimit(this);
                AddGapTick(XOf(previous.LastTimestamp));
                AddGapTick(XOf(column.FirstTimestamp));
            }

            if (strip.Kind != column.Kind)
            {
                strip.FlushStrip(this);
                strip.Kind = column.Kind;
                strip.StartX = x;
            }

            // Each column is one pixel wide; +1 covers its right half.
            strip.EndX = x + 1;

            if (column.MinPct is int minPct && column.MaxPct is int maxPct)
            {
                if (line.Kind != column.Kind || line.Points.Count == 0)
                {
                    line.FlushLine(this);
                    line.Kind = column.Kind;
                    line.StartX = x;
                }

                line.Points.Add(new Point(x, YOf(minPct)));
                line.Points.Add(new Point(x, YOf(maxPct)));
                line.EndX = x;
            }
            else
            {
                line.FlushLine(this);
            }

            if (_showLimitGuide)
            {
                if (column.HasLimit)
                {
                    if (limit.StartX < 0)
                    {
                        limit.StartX = x;
                    }

                    limit.EndX = x + 1;
                }
                else
                {
                    limit.FlushLimit(this);
                }
            }

            previous = column;
        }

        strip.FlushStrip(this);
        line.FlushLine(this);
        limit.FlushLimit(this);
    }

    // -------------------------------------------------------------- elements

    private sealed class Run
    {
        public Run(int capacity)
        {
            Points = new List<Point>(Math.Min(capacity, 1024));
        }

        public HistorySessionKind? Kind { get; set; }
        public double StartX { get; set; } = -1;
        public double EndX { get; set; }

        public List<Point> Points { get; }

        public void FlushStrip(HistoryChart owner)
        {
            if (Kind is HistorySessionKind kind && StartX >= 0)
            {
                owner.AddStrip(StartX, Math.Max(EndX, StartX), kind);
            }

            Kind = null;
            StartX = -1;
        }

        public void FlushLine(HistoryChart owner)
        {
            if (Kind is HistorySessionKind kind && Points.Count >= 2)
            {
                owner.AddLineRun(Points, kind);
            }

            Kind = null;
            StartX = -1;
            Points.Clear();
        }

        public void FlushLimit(HistoryChart owner)
        {
            if (StartX >= 0)
            {
                owner.AddLimitRun(StartX, Math.Max(EndX, StartX));
            }

            StartX = -1;
        }
    }

    private void AddLineRun(List<Point> points, HistorySessionKind kind)
    {
        // Area fill: the region under this run, closed at the baseline. One
        // flat 8%-opacity fill makes the level readable at a glance without
        // competing with the state colors. A run that is narrow relative to the
        // window has no region worth filling: its polygon collapses into a tall
        // vertical slab that reads as a bar, so the line and strip carry it.
        var runWidth = points[^1].X - points[0].X;
        if (runWidth >= Math.Max(24, PlotCanvas.ActualWidth * 0.04))
        {
            var area = new Polygon
            {
                Fill = _primaryBrush,
                Opacity = 0.08,
            };
            foreach (var point in points)
            {
                area.Points.Add(point);
            }

            area.Points.Add(new Point(points[^1].X, PlotTop + PlotHeight));
            area.Points.Add(new Point(points[0].X, PlotTop + PlotHeight));
            PlotCanvas.Children.Add(area);
        }

        var polyline = new Polyline
        {
            StrokeThickness = 2,
            Stroke = KindBrush(kind),
            StrokeLineJoin = PenLineJoin.Miter,
        };
        foreach (var point in points)
        {
            polyline.Points.Add(point);
        }

        if (IsIdleKind(kind))
        {
            // Long dashes, deliberately unlike the fine-dotted 80% guide: the
            // idle plateau sits exactly on that guide, so the two patterns must
            // not look like the same line.
            polyline.StrokeDashArray = new DoubleCollection { 4, 3 };
            polyline.StrokeDashCap = PenLineCap.Flat;
        }

        PlotCanvas.Children.Add(polyline);
    }

    private void AddStrip(double startX, double endX, HistorySessionKind kind)
    {
        var strip = new Rectangle
        {
            Width = Math.Max(1, endX - startX),
            Height = 4,
            Fill = KindBrush(kind),
        };
        Canvas.SetLeft(strip, startX);
        Canvas.SetTop(strip, PlotTop + PlotHeight - 4);
        PlotCanvas.Children.Add(strip);
    }

    private void AddGapTick(double x)
    {
        PlotCanvas.Children.Add(new Line
        {
            X1 = Snap(x),
            X2 = Snap(x),
            Y1 = PlotTop + PlotHeight - 6,
            Y2 = PlotTop + PlotHeight,
            StrokeThickness = 1,
            Stroke = _gridBrush,
        });
    }

    private void AddLimitRun(double startX, double endX)
    {
        PlotCanvas.Children.Add(new Line
        {
            X1 = startX,
            X2 = endX,
            Y1 = Snap(YOf(80)),
            Y2 = Snap(YOf(80)),
            StrokeThickness = 1,
            Stroke = _limitBrush,
            // Fine dots: an idle plateau parked on this line is drawn with long
            // dashes, so the guide stays legible underneath it.
            StrokeDashArray = new DoubleCollection { 1, 3 },
            StrokeDashCap = PenLineCap.Round,
        });
    }

    private Brush KindBrush(HistorySessionKind kind) => kind switch
    {
        HistorySessionKind.Charging => _chargingBrush,
        HistorySessionKind.Discharging => _dischargingBrush,
        _ => _idleBrush,
    };

    private static bool IsIdleKind(HistorySessionKind kind) =>
        kind is not (HistorySessionKind.Charging or HistorySessionKind.Discharging);

    // ----------------------------------------------------------------- hover

    private void PlotCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_samples.Count == 0 || _to <= _from)
        {
            return;
        }

        var position = e.GetCurrentPoint(PlotCanvas).Position;
        var plotLeft = GutterLeft;
        var plotRight = Math.Max(plotLeft + 10, PlotCanvas.ActualWidth - MarginRight);
        if (position.X < plotLeft || position.X > plotRight)
        {
            ClearHover();
            return;
        }

        var time = _from + TimeSpan.FromMilliseconds(
            (position.X - plotLeft) / (plotRight - plotLeft) * (_to - _from).TotalMilliseconds);

        // Nearest sample by timestamp; snap only when it is within the join
        // threshold, otherwise the pointer is inside a gap and the readout
        // says so instead of pretending a value applies there.
        var index = NearestSample(time);
        var nearest = _samples[index];

        if (nearest.Timestamp is DateTimeOffset snapped
            && Math.Abs((snapped - time).TotalMinutes) <= GapThreshold.TotalMinutes / 2)
        {
            DrawHover(snapped, nearest.Pct);
            HoverChanged?.Invoke(this, new HistoryHover(nearest, null, null));
            return;
        }

        // Gap: bracket the pointer with the surrounding samples.
        DateTimeOffset? gapStart = null;
        DateTimeOffset? gapEnd = null;
        for (var i = 0; i < _samples.Count; i++)
        {
            if (_samples[i].Timestamp is DateTimeOffset t && t <= time)
            {
                gapStart = t;
            }
        }

        for (var i = _samples.Count - 1; i >= 0; i--)
        {
            if (_samples[i].Timestamp is DateTimeOffset t && t >= time)
            {
                gapEnd = t;
                break;
            }
        }

        HoverCanvas.Children.Clear();
        HoverChanged?.Invoke(this, new HistoryHover(null, gapStart, gapEnd));
    }

    private void DrawHover(DateTimeOffset timestamp, int? pct)
    {
        HoverCanvas.Children.Clear();

        var x = XOf(timestamp);
        HoverCanvas.Children.Add(new Line
        {
            X1 = Snap(x),
            X2 = Snap(x),
            Y1 = PlotTop,
            Y2 = PlotTop + PlotHeight,
            StrokeThickness = 1,
            Stroke = _gridBrush,
        });

        if (pct is int level)
        {
            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                StrokeThickness = 1,
                Stroke = _primaryBrush,
                Fill = _cardBrush,
            };
            Canvas.SetLeft(dot, x - 2.5);
            Canvas.SetTop(dot, YOf(level) - 2.5);
            HoverCanvas.Children.Add(dot);
        }
    }

    private void ClearHover()
    {
        HoverCanvas.Children.Clear();
        HoverChanged?.Invoke(this, null);
    }

    // --------------------------------------------------------------- helpers

    private int NearestSample(DateTimeOffset time)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _samples.Count; i++)
        {
            if (_samples[i].Timestamp is not DateTimeOffset timestamp)
            {
                continue;
            }

            var distance = Math.Abs((timestamp - time).TotalMilliseconds);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private double XOf(DateTimeOffset timestamp)
    {
        var plotWidth = Math.Max(10, PlotCanvas.ActualWidth - MarginRight - GutterLeft);
        var fraction = (timestamp - _from).TotalMilliseconds / (_to - _from).TotalMilliseconds;
        return GutterLeft + Math.Clamp(fraction, 0, 1) * plotWidth;
    }

    private double YOf(int percent)
    {
        return PlotTop + (100 - Math.Clamp(percent, 0, 100)) * PlotHeight / 100.0;
    }

    private static double Snap(double value) => Math.Floor(value) + 0.5;

    private sealed class Column
    {
        public DateTimeOffset FirstTimestamp { get; set; }
        public DateTimeOffset LastTimestamp { get; set; }
        public HistorySessionKind Kind { get; set; }
        public int? MinPct { get; set; }
        public int? MaxPct { get; set; }
        public bool HasLimit { get; set; }
    }
}
