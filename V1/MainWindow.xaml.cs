using System;
using System.Windows;
using System.Windows.Input;
using US16x08_AbletonPanel.Models;
using US16x08_AbletonPanel.ViewModels;

namespace US16x08_AbletonPanel;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Closed += OnWindowClosed;
    }

    private void OnTrackStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TrackChannelModel track)
        {
            _viewModel.SelectedTrack = track;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
    }
}
