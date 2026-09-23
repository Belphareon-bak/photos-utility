using System.Windows;
using FotoArchiv.App.ViewModels;

namespace FotoArchiv.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
