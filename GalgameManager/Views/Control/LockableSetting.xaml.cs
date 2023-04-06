// Copyright (c) Microsoft Corporation and Contributors.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.CompilerServices;

using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace GalgameManager.Views.Control;

public sealed partial class LockableSetting : INotifyPropertyChanged
{
    public LockableSetting()
    {
        this.InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(LockableSetting), new PropertyMetadata(string.Empty));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(LockableSetting), new PropertyMetadata(string.Empty));


    public bool IsLock
    {
        get => (bool)GetValue(IsLockProperty);

        set
        {
            SetValue(IsLockProperty, value);
            OnPropertyChanged(nameof(IsEditable));
        }
    }

    public static readonly DependencyProperty IsLockProperty =
        DependencyProperty.Register(nameof(IsLock), typeof(bool), typeof(LockableSetting), new PropertyMetadata(false));

    public bool IsEditable => !IsLock;
        
        
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}