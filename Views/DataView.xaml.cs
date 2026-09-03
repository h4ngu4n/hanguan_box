using System;
using System.Linq;
using System.Windows.Controls;

namespace HanguanBox.Views;

public partial class DataView : UserControl
{
    public DataView()
    {
        InitializeComponent();

        var rnd = new Random(20260903);
        Bars.ItemsSource = new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" }
            .Select(d =>
            {
                var value = rnd.Next(90, 200);
                return new BarItem
                {
                    Label = d,
                    ValueHeight = value,
                    TargetHeight = value + rnd.Next(20, 60),
                    Value = value.ToString()
                };
            })
            .ToList();
    }

    public class BarItem
    {
        public string Label { get; set; } = "";
        public double ValueHeight { get; set; }
        public double TargetHeight { get; set; }
        public string Value { get; set; } = "";
    }
}
