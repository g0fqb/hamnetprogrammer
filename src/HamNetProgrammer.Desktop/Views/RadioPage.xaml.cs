using Microsoft.UI.Xaml.Controls;
using System.IO.Ports;

namespace HamNetProgrammer.Desktop.Views;

public sealed partial class RadioPage : Page
{
    public RadioPage()
    {
        this.InitializeComponent();
        RefreshPorts();
    }

    private void OnRefreshClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        PortListView.ItemsSource = SerialPort.GetPortNames();
    }
}
