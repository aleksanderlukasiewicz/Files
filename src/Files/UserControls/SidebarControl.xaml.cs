using Files.DataModels;
using Files.DataModels.NavigationControlItems;
using Files.Extensions;
using Files.Filesystem;
using Files.Filesystem.StorageItems;
using Files.Helpers;
using Files.Helpers.ContextFlyouts;
using Files.Services;
using Files.ViewModels;
using Microsoft.Toolkit.Mvvm.DependencyInjection;
using Microsoft.Toolkit.Mvvm.Input;
using Microsoft.Toolkit.Uwp;
using Microsoft.Toolkit.Uwp.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;

namespace Files.UserControls
{
    public sealed partial class SidebarControl : Microsoft.UI.Xaml.Controls.NavigationView
    {
        public IUserSettingsService UserSettingsService { get; } = Ioc.Default.GetService<IUserSettingsService>();

        public static SemaphoreSlim SideBarItemsSemaphore = new SemaphoreSlim(1, 1);

        public static BulkConcurrentObservableCollection<INavigationControlItem> SideBarItems { get; private set; } = new BulkConcurrentObservableCollection<INavigationControlItem>();

        public SettingsViewModel AppSettings => App.AppSettings;

        public delegate void SidebarItemInvokedEventHandler(object sender, SidebarItemInvokedEventArgs e);

        public event SidebarItemInvokedEventHandler SidebarItemInvoked;

        public delegate void SidebarItemNewPaneInvokedEventHandler(object sender, SidebarItemNewPaneInvokedEventArgs e);

        public event SidebarItemNewPaneInvokedEventHandler SidebarItemNewPaneInvoked;

        public delegate void SidebarItemPropertiesInvokedEventHandler(object sender, SidebarItemPropertiesInvokedEventArgs e);

        public event SidebarItemPropertiesInvokedEventHandler SidebarItemPropertiesInvoked;

        public delegate void SidebarItemDroppedEventHandler(object sender, SidebarItemDroppedEventArgs e);

        public event SidebarItemDroppedEventHandler SidebarItemDropped;

        /// <summary>
        /// The Model for the pinned sidebar items
        /// </summary>
        public SidebarPinnedModel SidebarPinnedModel => App.SidebarPinnedController.Model;

        public static readonly DependencyProperty EmptyRecycleBinCommandProperty = DependencyProperty.Register(nameof(EmptyRecycleBinCommand), typeof(ICommand), typeof(SidebarControl), new PropertyMetadata(null));

        public ICommand EmptyRecycleBinCommand
        {
            get => (ICommand)GetValue(EmptyRecycleBinCommandProperty);
            set => SetValue(EmptyRecycleBinCommandProperty, value);
        }

        public readonly RelayCommand CreateLibraryCommand = new RelayCommand(LibraryHelper.ShowCreateNewLibraryDialog);

        public readonly RelayCommand RestoreLibrariesCommand = new RelayCommand(LibraryHelper.ShowRestoreDefaultLibrariesDialog);

        public ICommand HideSectionCommand => new RelayCommand(HideSection);

        public ICommand UnpinItemCommand => new RelayCommand(UnpinItem);

        public ICommand MoveItemToTopCommand => new RelayCommand(MoveItemToTop);

        public ICommand MoveItemUpCommand => new RelayCommand(MoveItemUp);

        public ICommand MoveItemDownCommand => new RelayCommand(MoveItemDown);

        public ICommand MoveItemToBottomCommand => new RelayCommand(MoveItemToBottom);

        public ICommand OpenInNewTabCommand => new RelayCommand(OpenInNewTab);

        public ICommand OpenInNewWindowCommand => new RelayCommand(OpenInNewWindow);

        public ICommand OpenInNewPaneCommand => new RelayCommand(OpenInNewPane);

        public ICommand EjectDeviceCommand => new RelayCommand(EjectDevice);

        public ICommand OpenPropertiesCommand => new RelayCommand(OpenProperties);

        private bool IsInPointerPressed = false;

        private DispatcherQueueTimer dragOverSectionTimer, dragOverItemTimer;

        public SidebarControl()
        {
            this.InitializeComponent();
            this.Loaded += SidebarNavView_Loaded;

            dragOverSectionTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            dragOverItemTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        }

        public SidebarViewModel ViewModel
        {
            get => (SidebarViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        // Using a DependencyProperty as the backing store for ViewModel.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty ViewModelProperty =
            DependencyProperty.Register(nameof(ViewModel), typeof(SidebarViewModel), typeof(SidebarControl), new PropertyMetadata(null));

        public static readonly DependencyProperty SelectedSidebarItemProperty = DependencyProperty.Register(nameof(SelectedSidebarItem), typeof(INavigationControlItem), typeof(SidebarControl), new PropertyMetadata(null));

        public INavigationControlItem SelectedSidebarItem
        {
            get => (INavigationControlItem)GetValue(SelectedSidebarItemProperty);
            set
            {
                if (this.IsLoaded)
                {
                    SetValue(SelectedSidebarItemProperty, value);
                }
            }
        }

        public static readonly DependencyProperty TabContentProperty = DependencyProperty.Register(nameof(TabContent), typeof(UIElement), typeof(SidebarControl), new PropertyMetadata(null));

        public UIElement TabContent
        {
            get => (UIElement)GetValue(TabContentProperty);
            set => SetValue(TabContentProperty, value);
        }

        public static GridLength GetSidebarCompactSize()
        {
            if (App.Current.Resources.TryGetValue("NavigationViewCompactPaneLength", out object paneLength))
            {
                if (paneLength is double paneLengthDouble)
                {
                    return new GridLength(paneLengthDouble);
                }
            }
            return new GridLength(200);
        }

        private async void Sidebar_ItemInvoked(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewItemInvokedEventArgs args)
        {
            if (IsInPointerPressed || args.InvokedItem == null || args.InvokedItemContainer == null)
            {
                IsInPointerPressed = false;
                return;
            }

            string navigationPath = args.InvokedItemContainer.Tag?.ToString();

            if (await CheckEmptyDrive(navigationPath))
            {
                return;
            }

            var ctrlPressed = Window.Current.CoreWindow.GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
            if (ctrlPressed && navigationPath is not null)
            {
                await NavigationHelpers.OpenPathInNewTab(navigationPath);
                return;
            }

            SidebarItemInvoked?.Invoke(this, new SidebarItemInvokedEventArgs(args.InvokedItemContainer));
        }

        private async void Sidebar_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var properties = e.GetCurrentPoint(null).Properties;
            var context = (sender as Microsoft.UI.Xaml.Controls.NavigationViewItem).DataContext;
            if (properties.IsMiddleButtonPressed && context is INavigationControlItem item && item.Path != null)
            {
                if (await CheckEmptyDrive(item.Path))
                {
                    return;
                }
                IsInPointerPressed = true;
                e.Handled = true;
                await NavigationHelpers.OpenPathInNewTab(item.Path);
            }
        }

        private void PaneRoot_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ViewModel.IsLocationItem = false;
            ViewModel.ShowProperties = false;
            ViewModel.IsLibrariesHeader = false;
            ViewModel.ShowUnpinItem = false;
            ViewModel.ShowMoveItemUp = false;
            ViewModel.ShowMoveItemDown = false;
            ViewModel.ShowHideSection = false;
            ViewModel.ShowEjectDevice = false;
            ViewModel.ShowEmptyRecycleBin = false;
            ViewModel.CanOpenInNewPane = ViewModel.IsLocationItem && ViewModel.PaneHolder.IsMultiPaneEnabled;
            
            ViewModel.ShowSectionsToggles = true;
            ShowContextMenu(this, e);

            e.Handled = true;
        }

        private void NavigationViewLocationItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var sidebarItem = sender as Microsoft.UI.Xaml.Controls.NavigationViewItem;
            var item = sidebarItem.DataContext as LocationItem;

            bool drivesHeader = "Drives".GetLocalized().Equals(item.Text);
            bool networkDrivesHeader = "SidebarNetworkDrives".GetLocalized().Equals(item.Text);
            bool cloudDrivesHeader = "SidebarCloudDrives".GetLocalized().Equals(item.Text);
            bool librariesHeader = "SidebarLibraries".GetLocalized().Equals(item.Text);
            bool wslHeader = "WSL".GetLocalized().Equals(item.Text);
            bool fileTagsHeader = "FileTags".GetLocalized().Equals(item.Text);
            bool favoritesHeader = "SidebarFavorites".GetLocalized().Equals(item.Text);
            bool header = drivesHeader || networkDrivesHeader || cloudDrivesHeader || librariesHeader || wslHeader || fileTagsHeader || favoritesHeader;

            if (!header)
            {
                bool library = item.Section == SectionType.Library;
                bool favorite = item.Section == SectionType.Favorites;

                ViewModel.IsLocationItem = true;
                ViewModel.ShowProperties = true;
                ViewModel.IsLibrariesHeader = false;
                ViewModel.ShowUnpinItem = ((library || favorite) && !item.IsDefaultLocation);
                ViewModel.ShowMoveItemUp = ViewModel.ShowUnpinItem && App.SidebarPinnedController.Model.IndexOfItem(item) > 1;
                ViewModel.ShowMoveItemDown = ViewModel.ShowUnpinItem && App.SidebarPinnedController.Model.IndexOfItem(item) < App.SidebarPinnedController.Model.FavoriteItems.Count;
                ViewModel.ShowHideSection = false;
                ViewModel.ShowEjectDevice = false;
                ViewModel.CanOpenInNewPane = ViewModel.IsLocationItem && ViewModel.PaneHolder.IsMultiPaneEnabled;

                if (string.Equals(item.Path, "Home".GetLocalized(), StringComparison.OrdinalIgnoreCase))
                {
                    ViewModel.ShowProperties = false;
                }

                if (string.Equals(item.Path, CommonPaths.RecycleBinPath, StringComparison.OrdinalIgnoreCase))
                {
                    ViewModel.ShowEmptyRecycleBin = true;
                    ViewModel.ShowUnpinItem = true;
                    ViewModel.ShowProperties = false;
                }
                else
                {
                    ViewModel.ShowEmptyRecycleBin = false;
                }
            }
            else
            {
                ViewModel.IsLocationItem = false;
                ViewModel.ShowProperties = false;
                ViewModel.IsLibrariesHeader = librariesHeader;
                ViewModel.ShowUnpinItem = false;
                ViewModel.ShowMoveItemUp = false;
                ViewModel.ShowMoveItemDown = false;
                ViewModel.ShowHideSection = true;
                ViewModel.ShowEjectDevice = false;
                ViewModel.ShowEmptyRecycleBin = false;
                ViewModel.CanOpenInNewPane = ViewModel.IsLocationItem && ViewModel.PaneHolder.IsMultiPaneEnabled;
            }

            ViewModel.ShowSectionsToggles = false;
            ViewModel.RightClickedItem = item;

            ShowContextMenu((UIElement)sender, e, ViewModel.IsLocationItem);

            e.Handled = true;
        }

        private void NavigationViewDriveItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var sidebarItem = sender as Microsoft.UI.Xaml.Controls.NavigationViewItem;
            var item = sidebarItem.DataContext as DriveItem;

            ViewModel.IsLocationItem = true;
            ViewModel.IsLibrariesHeader = false;
            ViewModel.ShowEjectDevice = item.IsRemovable;
            ViewModel.ShowUnpinItem = false;
            ViewModel.ShowMoveItemUp = false;
            ViewModel.ShowMoveItemDown = false;
            ViewModel.ShowEmptyRecycleBin = false;
            ViewModel.ShowProperties = true;
            ViewModel.ShowHideSection = false;
            ViewModel.CanOpenInNewPane = ViewModel.IsLocationItem && ViewModel.PaneHolder.IsMultiPaneEnabled;
            
            ViewModel.ShowSectionsToggles = false;
            ViewModel.RightClickedItem = item;

            ShowContextMenu((UIElement)sender, e, true);

            e.Handled = true;
        }

        private void NavigationViewWSLItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var sidebarItem = sender as Microsoft.UI.Xaml.Controls.NavigationViewItem;
            var item = sidebarItem.DataContext as WslDistroItem;

            ViewModel.IsLocationItem = true;
            ViewModel.IsLibrariesHeader = false;
            ViewModel.ShowEjectDevice = false;
            ViewModel.ShowUnpinItem = false;
            ViewModel.ShowMoveItemUp = false;
            ViewModel.ShowMoveItemDown = false;
            ViewModel.ShowEmptyRecycleBin = false;
            ViewModel.ShowProperties = false;
            ViewModel.ShowHideSection = false;
            ViewModel.CanOpenInNewPane = ViewModel.IsLocationItem && ViewModel.PaneHolder.IsMultiPaneEnabled;
           
            ViewModel.ShowSectionsToggles = false;
            ViewModel.RightClickedItem = item;

            ShowContextMenu((UIElement)sender, e, false);

            e.Handled = true;
        }

        private void NavigationViewFileTagsItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var sidebarItem = sender as Microsoft.UI.Xaml.Controls.NavigationViewItem;
            var item = sidebarItem.DataContext as FileTagItem;

            ViewModel.IsLocationItem = true;
            ViewModel.IsLibrariesHeader = false;
            ViewModel.ShowEjectDevice = false;
            ViewModel.ShowUnpinItem = false;
            ViewModel.ShowMoveItemUp = false;
            ViewModel.ShowMoveItemDown = false;
            ViewModel.ShowEmptyRecycleBin = false;
            ViewModel.ShowProperties = false;
            ViewModel.ShowHideSection = false;
            ViewModel.CanOpenInNewPane = ViewModel.IsLocationItem && ViewModel.PaneHolder.IsMultiPaneEnabled;
            
            ViewModel.ShowSectionsToggles = true;
            ViewModel.RightClickedItem = item;

            ShowContextMenu((UIElement)sender, e, false);

            e.Handled = true;
        }

        private async void OpenInNewTab()
        {
            if (await CheckEmptyDrive(ViewModel.RightClickedItem.Path))
            {
                return;
            }
            await NavigationHelpers.OpenPathInNewTab(ViewModel.RightClickedItem.Path);
        }

        private async void OpenInNewPane()
        {
            if (await CheckEmptyDrive((ViewModel.RightClickedItem as INavigationControlItem)?.Path))
            {
                return;
            }
            SidebarItemNewPaneInvoked?.Invoke(this, new SidebarItemNewPaneInvokedEventArgs(ViewModel.RightClickedItem));
        }

        private async void OpenInNewWindow()
        {
            if (await CheckEmptyDrive(ViewModel.RightClickedItem.Path))
            {
                return;
            }
            await NavigationHelpers.OpenPathInNewWindowAsync(ViewModel.RightClickedItem.Path);
        }

        private void HideSection()
        {
            if ("SidebarFavorites".GetLocalized().Equals(ViewModel.RightClickedItem.Text))
            {
                UserSettingsService.AppearanceSettingsService.ShowFavoritesSection = false;
                App.SidebarPinnedController.Model.UpdateFavoritesSectionVisibility();
            }
            else if ("SidebarLibraries".GetLocalized().Equals(ViewModel.RightClickedItem.Text))
            {
                UserSettingsService.AppearanceSettingsService.ShowLibrarySection = false;
                App.LibraryManager.UpdateLibrariesSectionVisibility();
            }
            else if ("SidebarCloudDrives".GetLocalized().Equals(ViewModel.RightClickedItem.Text))
            {
                UserSettingsService.AppearanceSettingsService.ShowCloudDrivesSection = false;
                App.CloudDrivesManager.UpdateCloudDrivesSectionVisibility();
            }
            else if ("Drives".GetLocalized().Equals(ViewModel.RightClickedItem.Text))
            {
                UserSettingsService.AppearanceSettingsService.ShowDrivesSection = false;
                App.DrivesManager.UpdateDrivesSectionVisibility();
            }
            else if ("SidebarNetworkDrives".GetLocalized().Equals(ViewModel.RightClickedItem.Text))
            {
                UserSettingsService.AppearanceSettingsService.ShowNetworkDrivesSection = false;
                App.NetworkDrivesManager.UpdateNetworkDrivesSectionVisibility();
            }
            else if ("WSL".GetLocalized().Equals(ViewModel.RightClickedItem.Text))
            {
                UserSettingsService.AppearanceSettingsService.ShowWslSection = false;
                App.WSLDistroManager.UpdateWslSectionVisibility();
            }
            else if ("FileTags".GetLocalized().Equals(ViewModel.RightClickedItem.Text))
            {
                UserSettingsService.AppearanceSettingsService.ShowFileTagsSection = false;
                App.FileTagsManager.UpdateFileTagsSectionVisibility();
            }
        }

        private void UnpinItem()
        {
            if (string.Equals(CommonPaths.RecycleBinPath, ViewModel.RightClickedItem.Path, StringComparison.OrdinalIgnoreCase))
            {
                UserSettingsService.AppearanceSettingsService.PinRecycleBinToSidebar = false;
            }
            else if (ViewModel.RightClickedItem.Section == SectionType.Favorites)
            {
                App.SidebarPinnedController.Model.RemoveItem(ViewModel.RightClickedItem.Path);
            }
        }

        private void MoveItemToTop()
        {
            if (ViewModel.RightClickedItem.Section == SectionType.Favorites)
            {
                bool isSelectedSidebarItem = false;

                if (SelectedSidebarItem == ViewModel.RightClickedItem)
                {
                    isSelectedSidebarItem = true;
                }

                int oldIndex = App.SidebarPinnedController.Model.IndexOfItem(ViewModel.RightClickedItem);
                App.SidebarPinnedController.Model.MoveItem(ViewModel.RightClickedItem, oldIndex, 1);

                if (isSelectedSidebarItem)
                {
                    SetValue(SelectedSidebarItemProperty, ViewModel.RightClickedItem);
                }
            }
        }

        private void MoveItemUp()
        {
            if (ViewModel.RightClickedItem.Section == SectionType.Favorites)
            {
                bool isSelectedSidebarItem = false;

                if (SelectedSidebarItem == ViewModel.RightClickedItem)
                {
                    isSelectedSidebarItem = true;
                }

                int oldIndex = App.SidebarPinnedController.Model.IndexOfItem(ViewModel.RightClickedItem);
                App.SidebarPinnedController.Model.MoveItem(ViewModel.RightClickedItem, oldIndex, oldIndex - 1);

                if (isSelectedSidebarItem)
                {
                    SetValue(SelectedSidebarItemProperty, ViewModel.RightClickedItem);
                }
            }
        }

        private void MoveItemDown()
        {
            if (ViewModel.RightClickedItem.Section == SectionType.Favorites)
            {
                bool isSelectedSidebarItem = false;

                if (SelectedSidebarItem == ViewModel.RightClickedItem)
                {
                    isSelectedSidebarItem = true;
                }

                int oldIndex = App.SidebarPinnedController.Model.IndexOfItem(ViewModel.RightClickedItem);
                App.SidebarPinnedController.Model.MoveItem(ViewModel.RightClickedItem, oldIndex, oldIndex + 1);

                if (isSelectedSidebarItem)
                {
                    SetValue(SelectedSidebarItemProperty, ViewModel.RightClickedItem);
                }
            }
        }

        private void MoveItemToBottom()
        {
            if (ViewModel.RightClickedItem.Section == SectionType.Favorites)
            {
                bool isSelectedSidebarItem = false;

                if (SelectedSidebarItem == ViewModel.RightClickedItem)
                {
                    isSelectedSidebarItem = true;
                }

                int oldIndex = App.SidebarPinnedController.Model.IndexOfItem(ViewModel.RightClickedItem);
                App.SidebarPinnedController.Model.MoveItem(ViewModel.RightClickedItem, oldIndex, App.SidebarPinnedController.Model.FavoriteItems.Count);

                if (isSelectedSidebarItem)
                {
                    SetValue(SelectedSidebarItemProperty, ViewModel.RightClickedItem);
                }
            }
        }

        private void OpenProperties()
        {
            SidebarItemPropertiesInvoked?.Invoke(this, new SidebarItemPropertiesInvokedEventArgs(ViewModel.RightClickedItem));
        }

        private async void EjectDevice()
        {
            await DriveHelpers.EjectDeviceAsync(ViewModel.RightClickedItem.Path);
        }

        private void NavigationViewItem_DragStarting(UIElement sender, DragStartingEventArgs args)
        {
            if (!((sender as Microsoft.UI.Xaml.Controls.NavigationViewItem).DataContext is LocationItem locationItem))
            {
                return;
            }

            // Adding the original Location item dragged to the DragEvents data view
            var navItem = (sender as Microsoft.UI.Xaml.Controls.NavigationViewItem);
            args.Data.Properties.Add("sourceLocationItem", navItem);
        }

        private object dragOverSection, dragOverItem = null;

        private bool isDropOnProcess = false;

        private void NavigationViewItem_DragEnter(object sender, DragEventArgs e)
        {
            VisualStateManager.GoToState(sender as Microsoft.UI.Xaml.Controls.NavigationViewItem, "DragEnter", false);

            if ((sender as Microsoft.UI.Xaml.Controls.NavigationViewItem).DataContext is INavigationControlItem iNavItem)
            {
                if (string.IsNullOrEmpty(iNavItem.Path))
                {
                    dragOverSection = sender;
                    dragOverSectionTimer.Stop();
                    dragOverSectionTimer.Debounce(() =>
                    {
                        if (dragOverSection != null)
                        {
                            dragOverSectionTimer.Stop();
                            if ((dragOverSection as Microsoft.UI.Xaml.Controls.NavigationViewItem).DataContext is LocationItem section)
                            {
                                section.IsExpanded = true;
                            }
                            dragOverSection = null;
                        }
                    }, TimeSpan.FromMilliseconds(1000), false);
                }
                else
                {
                    dragOverItem = sender;
                    dragOverItemTimer.Stop();
                    dragOverItemTimer.Debounce(() =>
                    {
                        if (dragOverItem != null)
                        {
                            dragOverItemTimer.Stop();
                            SidebarItemInvoked?.Invoke(this, new SidebarItemInvokedEventArgs(dragOverItem as Microsoft.UI.Xaml.Controls.NavigationViewItemBase));
                            dragOverItem = null;
                        }
                    }, TimeSpan.FromMilliseconds(1000), false);
                }
            }
        }

        private void NavigationViewItem_DragLeave(object sender, DragEventArgs e)
        {
            VisualStateManager.GoToState(sender as Microsoft.UI.Xaml.Controls.NavigationViewItem, "DragLeave", false);

            isDropOnProcess = false;

            if ((sender as Microsoft.UI.Xaml.Controls.NavigationViewItem).DataContext is INavigationControlItem)
            {
                if (sender == dragOverItem)
                {
                    // Reset dragged over item
                    dragOverItem = null;
                }
                if (sender == dragOverSection)
                {
                    // Reset dragged over item
                    dragOverSection = null;
                }
            }
        }

        private async void NavigationViewLocationItem_DragOver(object sender, DragEventArgs e)
        {
            if (!((sender as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.DataContext is LocationItem locationItem))
            {
                return;
            }

            var deferral = e.GetDeferral();

            if (Filesystem.FilesystemHelpers.HasDraggedStorageItems(e.DataView))
            {
                e.Handled = true;
                isDropOnProcess = true;

                var handledByFtp = await Filesystem.FilesystemHelpers.CheckDragNeedsFulltrust(e.DataView);
                var storageItems = await Filesystem.FilesystemHelpers.GetDraggedStorageItems(e.DataView);

                if (string.IsNullOrEmpty(locationItem.Path) && SectionType.Favorites.Equals(locationItem.Section) && storageItems.Any())
                {
                    bool haveFoldersToPin = false;

                    foreach (var item in storageItems)
                    {
                        if (item.ItemType == FilesystemItemType.Directory && !SidebarPinnedModel.FavoriteItems.Contains(item.Path))
                        {
                            haveFoldersToPin = true;
                            break;
                        }
                    }

                    if (!haveFoldersToPin)
                    {
                        e.AcceptedOperation = DataPackageOperation.None;
                    }
                    else
                    {
                        e.DragUIOverride.IsCaptionVisible = true;
                        e.DragUIOverride.Caption = "BaseLayoutItemContextFlyoutPinToFavorites/Text".GetLocalized();
                        e.AcceptedOperation = DataPackageOperation.Move;
                    }
                }
                else if (string.IsNullOrEmpty(locationItem.Path) ||
                    (storageItems.Any() && storageItems.AreItemsAlreadyInFolder(locationItem.Path))
                    || locationItem.Path.StartsWith("Home".GetLocalized(), StringComparison.OrdinalIgnoreCase))
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                }
                else if (handledByFtp)
                {
                    if (locationItem.Path.StartsWith(CommonPaths.RecycleBinPath, StringComparison.Ordinal))
                    {
                        e.AcceptedOperation = DataPackageOperation.None;
                    }
                    else
                    {
                        e.DragUIOverride.IsCaptionVisible = true;
                        e.DragUIOverride.Caption = string.Format("CopyToFolderCaptionText".GetLocalized(), locationItem.Text);
                        e.AcceptedOperation = DataPackageOperation.Copy;
                    }
                }
                else if (!storageItems.Any())
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                }
                else
                {
                    e.DragUIOverride.IsCaptionVisible = true;
                    if (locationItem.Path.StartsWith(CommonPaths.RecycleBinPath, StringComparison.Ordinal))
                    {
                        e.DragUIOverride.Caption = string.Format("MoveToFolderCaptionText".GetLocalized(), locationItem.Text);
                        e.AcceptedOperation = DataPackageOperation.Move;
                    }
                    else if (e.Modifiers.HasFlag(DragDropModifiers.Alt) || e.Modifiers.HasFlag(DragDropModifiers.Control | DragDropModifiers.Shift))
                    {
                        e.DragUIOverride.Caption = string.Format("LinkToFolderCaptionText".GetLocalized(), locationItem.Text);
                        e.AcceptedOperation = DataPackageOperation.Link;
                    }
                    else if (e.Modifiers.HasFlag(DragDropModifiers.Control))
                    {
                        e.DragUIOverride.Caption = string.Format("CopyToFolderCaptionText".GetLocalized(), locationItem.Text);
                        e.AcceptedOperation = DataPackageOperation.Copy;
                    }
                    else if (e.Modifiers.HasFlag(DragDropModifiers.Shift))
                    {
                        e.DragUIOverride.Caption = string.Format("MoveToFolderCaptionText".GetLocalized(), locationItem.Text);
                        e.AcceptedOperation = DataPackageOperation.Move;
                    }
                    else if (storageItems.Any(x => x.Item is ZipStorageFile || x.Item is ZipStorageFolder)
                        || ZipStorageFolder.IsZipPath(locationItem.Path))
                    {
                        e.DragUIOverride.Caption = string.Format("CopyToFolderCaptionText".GetLocalized(), locationItem.Text);
                        e.AcceptedOperation = DataPackageOperation.Copy;
                    }
                    else if (storageItems.AreItemsInSameDrive(locationItem.Path) || locationItem.IsDefaultLocation)
                    {
                        e.AcceptedOperation = DataPackageOperation.Move;
                        e.DragUIOverride.Caption = string.Format("MoveToFolderCaptionText".GetLocalized(), locationItem.Text);
                    }
                    else
                    {
                        e.AcceptedOperation = DataPackageOperation.Copy;
                        e.DragUIOverride.Caption = string.Format("CopyToFolderCaptionText".GetLocalized(), locationItem.Text);
                    }
                }
            }
            else if ((e.DataView.Properties["sourceLocationItem"] as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.DataContext is LocationItem sourceLocationItem)
            {
                // else if the drag over event is called over a location item
                NavigationViewLocationItem_DragOver_SetCaptions(locationItem, sourceLocationItem, e);
            }

            deferral.Complete();
        }

        /// <summary>
        /// Sets the captions when dragging a location item over another location item
        /// </summary>
        /// <param name="senderLocationItem">The location item which fired the DragOver event</param>
        /// <param name="sourceLocationItem">The source location item</param>
        /// <param name="e">DragEvent args</param>
        private void NavigationViewLocationItem_DragOver_SetCaptions(LocationItem senderLocationItem, LocationItem sourceLocationItem, DragEventArgs e)
        {
            // If the location item is the same as the original dragged item
            if (sourceLocationItem.Equals(senderLocationItem))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.DragUIOverride.IsCaptionVisible = false;
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.Move;
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.Caption = "PinToSidebarByDraggingCaptionText".GetLocalized();
            }
        }

        private bool lockFlag = false;

        private async void NavigationViewLocationItem_Drop(object sender, DragEventArgs e)
        {
            if (lockFlag)
            {
                return;
            }
            lockFlag = true;

            dragOverItem = null; // Reset dragged over item
            dragOverSection = null; // Reset dragged over section

            if (!((sender as Microsoft.UI.Xaml.Controls.NavigationViewItem).DataContext is LocationItem locationItem))
            {
                return;
            }

            // If the dropped item is a folder or file from a file system
            if (FilesystemHelpers.HasDraggedStorageItems(e.DataView))
            {
                VisualStateManager.GoToState(sender as Microsoft.UI.Xaml.Controls.NavigationViewItem, "Drop", false);

                var deferral = e.GetDeferral();

                if (string.IsNullOrEmpty(locationItem.Path) && SectionType.Favorites.Equals(locationItem.Section) && isDropOnProcess) // Pin to Favorites section
                {
                    var storageItems = await Filesystem.FilesystemHelpers.GetDraggedStorageItems(e.DataView);
                    foreach (var item in storageItems)
                    {
                        if (item.ItemType == FilesystemItemType.Directory && !SidebarPinnedModel.FavoriteItems.Contains(item.Path))
                        {
                            SidebarPinnedModel.AddItem(item.Path);
                        }
                    }
                }
                else
                {
                    var signal = new AsyncManualResetEvent();
                    SidebarItemDropped?.Invoke(this, new SidebarItemDroppedEventArgs()
                    {
                        Package = e.DataView,
                        ItemPath = locationItem.Path,
                        AcceptedOperation = e.AcceptedOperation,
                        SignalEvent = signal
                    });
                    await signal.WaitAsync();
                }

                isDropOnProcess = false;
                deferral.Complete();
            }
            else if ((e.DataView.Properties["sourceLocationItem"] as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.DataContext is LocationItem sourceLocationItem)
            {
                // Else if the dropped item is a location item

                // Swap the two items
                SidebarPinnedModel.SwapItems(sourceLocationItem, locationItem);
            }

            await Task.Yield();
            lockFlag = false;
        }

        private async void NavigationViewDriveItem_DragOver(object sender, DragEventArgs e)
        {
            if (!((sender as Microsoft.UI.Xaml.Controls.NavigationViewItem).DataContext is DriveItem driveItem) ||
                !Filesystem.FilesystemHelpers.HasDraggedStorageItems(e.DataView))
            {
                return;
            }

            var deferral = e.GetDeferral();
            e.Handled = true;

            var handledByFtp = await Filesystem.FilesystemHelpers.CheckDragNeedsFulltrust(e.DataView);
            var storageItems = await Filesystem.FilesystemHelpers.GetDraggedStorageItems(e.DataView);

            if ("DriveCapacityUnknown".GetLocalized().Equals(driveItem.SpaceText, StringComparison.OrdinalIgnoreCase) ||
                (storageItems.Any() && storageItems.AreItemsAlreadyInFolder(driveItem.Path)))
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
            else if (handledByFtp)
            {
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.Caption = string.Format("CopyToFolderCaptionText".GetLocalized(), driveItem.Text);
                e.AcceptedOperation = DataPackageOperation.Copy;
            }
            else if (!storageItems.Any())
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
            else
            {
                e.DragUIOverride.IsCaptionVisible = true;
                if (e.Modifiers.HasFlag(DragDropModifiers.Alt) || e.Modifiers.HasFlag(DragDropModifiers.Control | DragDropModifiers.Shift))
                {
                    e.DragUIOverride.Caption = string.Format("LinkToFolderCaptionText".GetLocalized(), driveItem.Text);
                    e.AcceptedOperation = DataPackageOperation.Link;
                }
                else if (e.Modifiers.HasFlag(DragDropModifiers.Control))
                {
                    e.DragUIOverride.Caption = string.Format("CopyToFolderCaptionText".GetLocalized(), driveItem.Text);
                    e.AcceptedOperation = DataPackageOperation.Copy;
                }
                else if (e.Modifiers.HasFlag(DragDropModifiers.Shift))
                {
                    e.DragUIOverride.Caption = string.Format("MoveToFolderCaptionText".GetLocalized(), driveItem.Text);
                    e.AcceptedOperation = DataPackageOperation.Move;
                }
                else if (storageItems.AreItemsInSameDrive(driveItem.Path))
                {
                    e.AcceptedOperation = DataPackageOperation.Move;
                    e.DragUIOverride.Caption = string.Format("MoveToFolderCaptionText".GetLocalized(), driveItem.Text);
                }
                else
                {
                    e.AcceptedOperation = DataPackageOperation.Copy;
                    e.DragUIOverride.Caption = string.Format("CopyToFolderCaptionText".GetLocalized(), driveItem.Text);
                }
            }

            deferral.Complete();
        }

        private async void NavigationViewDriveItem_Drop(object sender, DragEventArgs e)
        {
            if (lockFlag)
            {
                return;
            }
            lockFlag = true;

            dragOverItem = null; // Reset dragged over item
            dragOverSection = null; // Reset dragged over section

            if (!((sender as Microsoft.UI.Xaml.Controls.NavigationViewItem).DataContext is DriveItem driveItem))
            {
                return;
            }

            VisualStateManager.GoToState(sender as Microsoft.UI.Xaml.Controls.NavigationViewItem, "Drop", false);

            var deferral = e.GetDeferral();

            var signal = new AsyncManualResetEvent();
            SidebarItemDropped?.Invoke(this, new SidebarItemDroppedEventArgs()
            {
                Package = e.DataView,
                ItemPath = driveItem.Path,
                AcceptedOperation = e.AcceptedOperation,
                SignalEvent = signal
            });
            await signal.WaitAsync();

            deferral.Complete();
            await Task.Yield();
            lockFlag = false;
        }

        private async void NavigationViewFileTagItem_DragOver(object sender, DragEventArgs e)
        {
            if (!((sender as Microsoft.UI.Xaml.Controls.NavigationViewItem).DataContext is FileTagItem fileTagItem) ||
                !Filesystem.FilesystemHelpers.HasDraggedStorageItems(e.DataView))
            {
                return;
            }

            var deferral = e.GetDeferral();
            e.Handled = true;

            var handledByFtp = await Filesystem.FilesystemHelpers.CheckDragNeedsFulltrust(e.DataView);
            var storageItems = await Filesystem.FilesystemHelpers.GetDraggedStorageItems(e.DataView);

            if (handledByFtp)
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
            else if (!storageItems.Any())
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
            else
            {
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.Caption = string.Format("LinkToFolderCaptionText".GetLocalized(), fileTagItem.Text);
                e.AcceptedOperation = DataPackageOperation.Link;
            }

            deferral.Complete();
        }

        private async void NavigationViewFileTag_Drop(object sender, DragEventArgs e)
        {
            if (lockFlag)
            {
                return;
            }
            lockFlag = true;

            dragOverItem = null; // Reset dragged over item
            dragOverSection = null; // Reset dragged over section

            if (!((sender as Microsoft.UI.Xaml.Controls.NavigationViewItem).DataContext is FileTagItem fileTagItem))
            {
                return;
            }

            VisualStateManager.GoToState(sender as Microsoft.UI.Xaml.Controls.NavigationViewItem, "Drop", false);

            var deferral = e.GetDeferral();

            var handledByFtp = await Filesystem.FilesystemHelpers.CheckDragNeedsFulltrust(e.DataView);
            var storageItems = await Filesystem.FilesystemHelpers.GetDraggedStorageItems(e.DataView);

            if (handledByFtp)
            {
                return;
            }

            foreach (var item in storageItems.Where(x => !string.IsNullOrEmpty(x.Path)))
            {
                var listedItem = new ListedItem(null) { ItemPath = item.Path };
                listedItem.FileFRN = await FileTagsHelper.GetFileFRN(item.Item);
                listedItem.FileTag = fileTagItem.FileTag.Uid;
            }

            deferral.Complete();
            await Task.Yield();
            lockFlag = false;
        }

        private void SidebarNavView_Loaded(object sender, RoutedEventArgs e)
        {
            (this.FindDescendant("TabContentBorder") as Border).Child = TabContent;

            DisplayModeChanged += SidebarControl_DisplayModeChanged;
        }

        private void SidebarControl_DisplayModeChanged(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewDisplayModeChangedEventArgs args)
        {
            IsPaneToggleButtonVisible = args.DisplayMode == Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode.Minimal;
        }

        private void Border_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            var step = 1;
            var ctrl = Window.Current.CoreWindow.GetKeyState(VirtualKey.Control);
            originalSize = IsPaneOpen ? UserSettingsService.AppearanceSettingsService.SidebarWidth : CompactPaneLength;

            if (ctrl.HasFlag(CoreVirtualKeyStates.Down))
            {
                step = 5;
            }

            if (e.Key == VirtualKey.Space || e.Key == VirtualKey.Enter)
            {
                IsPaneOpen = !IsPaneOpen;
                return;
            }

            if (IsPaneOpen)
            {
                if (e.Key == VirtualKey.Left)
                {
                    SetSize(-step, true);
                    e.Handled = true;
                }
                else if (e.Key == VirtualKey.Right)
                {
                    SetSize(step, true);
                    e.Handled = true;
                }
            }
            else if (e.Key == VirtualKey.Right)
            {
                IsPaneOpen = !IsPaneOpen;
                return;
            }

            UserSettingsService.AppearanceSettingsService.SidebarWidth = OpenPaneLength;
        }

        /// <summary>
        /// true if the user is currently resizing the sidebar
        /// </summary>
        private bool dragging;

        private double originalSize = 0;

        private void Border_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            if (DisplayMode == Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode.Expanded)
            {
                SetSize(e.Cumulative.Translation.X);
            }
        }

        private void Border_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!dragging) // keep showing pressed event if currently resizing the sidebar
            {
                Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.Arrow, 0);
                VisualStateManager.GoToState((sender as Grid).FindAscendant<SplitView>(), "ResizerNormal", true);
            }
        }

        private void SetSize(double val, bool closeImmediatleyOnOversize = false)
        {
            if (IsPaneOpen)
            {
                var newSize = originalSize + val;
                if (newSize <= Constants.UI.MaximumSidebarWidth && newSize >= Constants.UI.MinimumSidebarWidth)
                {
                    OpenPaneLength = newSize; // passing a negative value will cause an exception
                }

                if (newSize < Constants.UI.MinimumSidebarWidth) // if the new size is below the minimum, check whether to toggle the pane
                {
                    if (Constants.UI.MinimumSidebarWidth + val <= CompactPaneLength || closeImmediatleyOnOversize) // collapse the sidebar
                    {
                        IsPaneOpen = false;
                    }
                }
            }
            else
            {
                if (val >= Constants.UI.MinimumSidebarWidth - CompactPaneLength || closeImmediatleyOnOversize)
                {
                    OpenPaneLength = Constants.UI.MinimumSidebarWidth + (val + CompactPaneLength - Constants.UI.MinimumSidebarWidth); // set open sidebar length to minimum value to keep it smooth
                    IsPaneOpen = true;
                }
            }
        }

        private void Border_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (DisplayMode == Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode.Expanded)
            {
                Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.SizeWestEast, 0);
                VisualStateManager.GoToState((sender as Grid).FindAscendant<SplitView>(), "ResizerPointerOver", true);
            }
        }

        private void ResizeElementBorder_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.Arrow, 0);
            VisualStateManager.GoToState((sender as Grid).FindAscendant<SplitView>(), "ResizerNormal", true);
            UserSettingsService.AppearanceSettingsService.SidebarWidth = OpenPaneLength;
            dragging = false;
        }

        private void ResizeElementBorder_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            IsPaneOpen = !IsPaneOpen;
        }

        private void ResizeElementBorder_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            if (DisplayMode == Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode.Expanded)
            {
                originalSize = IsPaneOpen ? UserSettingsService.AppearanceSettingsService.SidebarWidth : CompactPaneLength;
                Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.SizeWestEast, 0);
                VisualStateManager.GoToState((sender as Grid).FindAscendant<SplitView>(), "ResizerPressed", true);
                dragging = true;
            }
        }

        private async Task<bool> CheckEmptyDrive(string drivePath)
        {
            if (drivePath is not null)
            {
                var matchingDrive = App.DrivesManager.Drives.FirstOrDefault(x => drivePath.StartsWith(x.Path, StringComparison.Ordinal));
                if (matchingDrive != null && matchingDrive.Type == DriveType.CDRom && matchingDrive.MaxSpace == ByteSizeLib.ByteSize.FromBytes(0))
                {
                    bool ejectButton = await DialogDisplayHelper.ShowDialogAsync("InsertDiscDialog/Title".GetLocalized(), string.Format("InsertDiscDialog/Text".GetLocalized(), matchingDrive.Path), "InsertDiscDialog/OpenDriveButton".GetLocalized(), "Close".GetLocalized());
                    if (ejectButton)
                    {
                        await DriveHelpers.EjectDeviceAsync(matchingDrive.Path);
                    }
                    return true;
                }
            }
            return false;
        }

        private async void LoadShellMenuItems(MenuFlyout itemContextMenuFlyout)
        {
            try
            {
                if (ViewModel.ShowEmptyRecycleBin)
                {
                    var emptyRecycleBinItem = itemContextMenuFlyout.Items.FirstOrDefault(x => x is MenuFlyoutItemBase menuFlyoutItem && (menuFlyoutItem.Tag as string) == "EmptyRecycleBin") as MenuFlyoutItemBase;
                    if (emptyRecycleBinItem is not null)
                    {
                        var binHasItems = await new RecycleBinHelpers().RecycleBinHasItems();
                        emptyRecycleBinItem.IsEnabled = binHasItems;
                    }
                }
                if (ViewModel.IsLocationItem)
                {
                    var shiftPressed = Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
                    var shellMenuItems = await ContextFlyoutItemHelper.GetItemContextShellCommandsAsync(connection: await AppServiceConnectionHelper.Instance, currentInstanceViewModel: null, workingDir: null,
                        new List<ListedItem>() { new ListedItem(null) { ItemPath = ViewModel.RightClickedItem.Path } }, shiftPressed: shiftPressed, showOpenMenu: false);

                    var shellItems = ItemModelListToContextFlyoutHelper.GetMenuFlyoutItemsFromModel(shellMenuItems);
                    shellItems.ForEach(shellItem => shellItem.Tag = "ShellItem");

                    if (!UserSettingsService.AppearanceSettingsService.MoveOverflowMenuItemsToSubMenu)
                    {
                        if (shellItems.Any())
                        {
                            var openedPopups = Windows.UI.Xaml.Media.VisualTreeHelper.GetOpenPopups(Window.Current);
                            var secondaryMenu = openedPopups.FirstOrDefault(popup => popup.Name == "OverflowPopup");
                            var itemsControl = secondaryMenu?.Child.FindDescendant<ItemsControl>();
                            if (itemsControl is not null)
                            {
                                shellItems.OfType<FrameworkElement>().ForEach(x => x.MaxWidth = itemsControl.ActualWidth - Constants.UI.ContextMenuLabelMargin); // Set items max width to current menu width (#5555)
                            }

                            itemContextMenuFlyout.Items.Add(new MenuFlyoutSeparator() { Tag = "ShellItem" }); ;
                            shellItems.ForEach(i => itemContextMenuFlyout.Items.Add(i));
                        }
                    }
                    else
                    {
                        var overflowItem = itemContextMenuFlyout.Items.FirstOrDefault(x => x is MenuFlyoutItemBase menuFlyoutItem && (menuFlyoutItem.Tag as string) == "ItemOverflow") as MenuFlyoutItemBase;
                        if (overflowItem is not null)
                        {
                            shellItems.ForEach(i => (overflowItem as MenuFlyoutSubItem).Items.Add(i));
                            ViewModel.ShowOverflowButton= shellItems.Any();
                        }
                    }
                }
            }
            catch { }
        }

        private void ShowContextMenu(UIElement sender, RightTappedRoutedEventArgs e, bool loadShellItems = false)
        {

            var contextMenu = FlyoutBase.GetAttachedFlyout(this) as MenuFlyout;
            contextMenu.ShowAt(sender, new FlyoutShowOptions() { Position = e.GetPosition(sender) });

            ViewModel.ShowOverflowButton = false;

            if (loadShellItems)
            {
               LoadShellMenuItems(contextMenu);
            }
        }

        private void HideContextMenu(MenuFlyout flyout)
        {
            //cleaning contextMenu from shell and overflow item

            if (!UserSettingsService.AppearanceSettingsService.MoveOverflowMenuItemsToSubMenu)
            {
                    var oldShellItems = flyout.Items.Where((i) => i.Tag != null && i.Tag.ToString() == "ShellItem");
                    oldShellItems.Reverse().ForEach((item) => flyout.Items.Remove(item));
            }
            else
            {
                var overflowItem = flyout.Items.FirstOrDefault(x => x is MenuFlyoutItemBase menuFlyoutItem && (menuFlyoutItem.Tag as string) == "ItemOverflow") as MenuFlyoutSubItem;
                if (overflowItem is not null)
                {
                    overflowItem.Items.Clear();
                }
            }

            ViewModel.ShowOverflowButton = false;
        }

        private void SidebarContextMenu_Closing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
        {
            HideContextMenu((MenuFlyout)sender);
        }
    }

    public class SidebarItemDroppedEventArgs : EventArgs
    {
        public DataPackageView Package { get; set; }
        public string ItemPath { get; set; }
        public DataPackageOperation AcceptedOperation { get; set; }
        public AsyncManualResetEvent SignalEvent { get; set; }
    }

    public class SidebarItemInvokedEventArgs : EventArgs
    {
        public Microsoft.UI.Xaml.Controls.NavigationViewItemBase InvokedItemContainer { get; set; }

        public SidebarItemInvokedEventArgs(Microsoft.UI.Xaml.Controls.NavigationViewItemBase ItemContainer)
        {
            InvokedItemContainer = ItemContainer;
        }
    }

    public class SidebarItemPropertiesInvokedEventArgs : EventArgs
    {
        public object InvokedItemDataContext { get; set; }

        public SidebarItemPropertiesInvokedEventArgs(object invokedItemDataContext)
        {
            InvokedItemDataContext = invokedItemDataContext;
        }
    }

    public class SidebarItemNewPaneInvokedEventArgs : EventArgs
    {
        public object InvokedItemDataContext { get; set; }

        public SidebarItemNewPaneInvokedEventArgs(object invokedItemDataContext)
        {
            InvokedItemDataContext = invokedItemDataContext;
        }
    }

    public class NavItemDataTemplateSelector : DataTemplateSelector
    {
        public DataTemplate LocationNavItemTemplate { get; set; }
        public DataTemplate DriveNavItemTemplate { get; set; }
        public DataTemplate LinuxNavItemTemplate { get; set; }
        public DataTemplate FileTagNavItemTemplate { get; set; }
        public DataTemplate HeaderNavItemTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            if (item != null && item is INavigationControlItem)
            {
                INavigationControlItem navControlItem = item as INavigationControlItem;
                switch (navControlItem.ItemType)
                {
                    case NavigationControlItemType.Location:
                        return LocationNavItemTemplate;

                    case NavigationControlItemType.Drive:
                        return DriveNavItemTemplate;

                    case NavigationControlItemType.CloudDrive:
                        return DriveNavItemTemplate;

                    case NavigationControlItemType.LinuxDistro:
                        return LinuxNavItemTemplate;

                    case NavigationControlItemType.FileTag:
                        return FileTagNavItemTemplate;

                    case NavigationControlItemType.Header:
                        return HeaderNavItemTemplate;
                }
            }
            return null;
        }
    }
}