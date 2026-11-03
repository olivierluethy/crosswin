using System;
using System.ComponentModel;
using System.Windows;

namespace HelloWpf;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private int _clicks;
    private string _note = "Data binding works";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        ContentRendered += (_, _) => Console.WriteLine("CROSSWIN_READY WPF window rendered");
    }

    public string Note
    {
        get => _note;
        set { _note = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Note))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnClick(object sender, RoutedEventArgs e) => Counter.Text = $"Clicks: {++_clicks}";
}
