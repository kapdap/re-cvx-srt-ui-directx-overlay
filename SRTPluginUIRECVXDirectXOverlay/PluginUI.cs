using GameOverlay.Drawing;
using GameOverlay.Windows;
using SRTPluginBase;
using SRTPluginProviderRECVX;
using SRTPluginProviderRECVX.Enumerations;
using SRTPluginProviderRECVX.Models;
using SRTPluginUIRECVXDirectXOverlay.Utilities;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace SRTPluginUIRECVXDirectXOverlay
{
    public class PluginUI : PluginBase, IPluginUI
    {
        public string RequiredProvider => "SRTPluginProviderRECVX";

        internal static PluginInfo _info = new PluginInfo();
        public override IPluginInfo Info => _info;

        public static PluginConfig Config;

        private IPluginHostDelegates _hostDelegates;
        private IGameMemoryRECVX _gameMemory;

        private OverlayWindow _window;
        private Graphics _graphics;
        private SharpDX.Direct2D1.WindowRenderTarget _device;

        private IntPtr _windowEventHook;
        private GCHandle _windowEventGCHandle;
        private Dispatcher _windowEventDispatcher;

        private Font _consolas32Bold; // IGT
        private Font _consolas14Bold; // HP
        private Font _consolas16Bold; // Default

        private SolidBrush _black;
        private SolidBrush _white;
        private SolidBrush _yellow;
        private SolidBrush _green;
        private SolidBrush _lawngreen;
        private SolidBrush _red;
        private SolidBrush _darkred;
        private SolidBrush _grey;
        private SolidBrush _darkergrey;
        private SolidBrush _gold;
        private SolidBrush _goldenrod;
        private SolidBrush _violet;

        private IReadOnlyDictionary<ItemEnumeration, SharpDX.Mathematics.Interop.RawRectangleF> _inventoryToImageTranslation;
        private SharpDX.Direct2D1.Bitmap _inventorySheet;

        private int ICON_SLOT_WIDTH = 96;
        private int ICON_SLOT_HEIGHT = 96;

        private bool _isOverlayInitialized;
        private bool _isOverlayReady;

        [STAThread]
        public override int Startup(IPluginHostDelegates hostDelegates)
        {
            _hostDelegates = hostDelegates;
            Config = LoadConfiguration<PluginConfig>();

            try
            {
                GenerateClipping();
                InitializeOverlay();
            }
            catch (Exception ex)
            {
                _hostDelegates.ExceptionMessage(ex);
                _inventoryToImageTranslation = null;

                _graphics?.Dispose();
                _graphics = null;
                _window?.Dispose();
                _window = null;

                _isOverlayInitialized = false;
                _isOverlayReady = false;

                return 1;
            }

            try
            {
                Thread t = new Thread(new ThreadStart(() =>
                {
                    _windowEventDispatcher = Dispatcher.CurrentDispatcher;
                    Dispatcher.Run();
                }))
                {
                    IsBackground = true
                };

                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            }
            catch (Exception ex)
            {
                _hostDelegates.ExceptionMessage(ex);
                _windowEventDispatcher = null;
            }

            return 0;
        }

        public override int Shutdown()
        {
            SaveConfiguration(Config);

            try
            {
                if (_windowEventGCHandle.IsAllocated)
                    _windowEventGCHandle.Free();

                if (_windowEventHook != IntPtr.Zero)
                    WinEventHook.WinEventUnhook(_windowEventHook);

                _windowEventDispatcher?.InvokeShutdown();
            }
            catch (Exception ex)
            {
                _hostDelegates.ExceptionMessage(ex);
            }

            _black?.Dispose();
            _white?.Dispose();
            _yellow?.Dispose();
            _green?.Dispose();
            _lawngreen?.Dispose();
            _grey?.Dispose();
            _darkergrey?.Dispose();
            _red?.Dispose();
            _darkred?.Dispose();
            _gold?.Dispose();
            _goldenrod?.Dispose();
            _violet?.Dispose();

            _consolas14Bold?.Dispose();
            _consolas16Bold?.Dispose();
            _consolas32Bold?.Dispose();

            _inventorySheet?.Dispose();
            _inventoryToImageTranslation = null;

            _windowEventHook = IntPtr.Zero;
            _windowEventDispatcher = null;

            _device = null;
            _graphics?.Dispose();
            _graphics = null;
            _window?.Dispose();
            _window = null;

            _isOverlayInitialized = false;
            _isOverlayReady = false;

            return 0;
        }

        public int ReceiveData(object gameMemory)
        {
            try
            {
                _gameMemory = (IGameMemoryRECVX)gameMemory;
                _gameMemory.Emulator.DetectGameWindowHandle = true;

                if (_isOverlayReady)
                {
                    UpdateOverlay();
                    RenderOverlay();
                }
                else
                    CreateOverlay();
            }
            catch (Exception ex)
            {
                _hostDelegates.ExceptionMessage(ex);
            }
            finally
            {
                if (_graphics != null && _graphics.IsInitialized)
                    _graphics.EndScene();
            }

            return 0;
        }

        private void InitializeOverlay()
        {
            DEVMODE devMode = default;
            devMode.dmSize = (short)Marshal.SizeOf<DEVMODE>();
            NativeWrappers.EnumDisplaySettings(null, -1, ref devMode);

            _window = new OverlayWindow(0, 0, devMode.dmPelsWidth, devMode.dmPelsHeight)
            {
                IsTopmost = true,
                IsVisible = true
            };

            _graphics = new Graphics()
            {
                MeasureFPS = false,
                PerPrimitiveAntiAliasing = false,
                TextAntiAliasing = true,
                UseMultiThreadedFactories = false,
                VSync = false
            };

            _isOverlayInitialized = true;
        }

        private bool CreateOverlay()
        {
            if (_isOverlayInitialized && !_isOverlayReady && _gameMemory.Emulator.GameWindowHandle != IntPtr.Zero)
            {
                _window.Create();

                _graphics.Width = _window.Width;
                _graphics.Height = _window.Height;
                _graphics.WindowHandle = _window.Handle;
                _graphics.Setup();

                _window.SizeChanged += (object sender, OverlaySizeEventArgs e) =>
                    _graphics.Resize(_window.Width, _window.Height);

                _window.FitTo(_gameMemory.Emulator.GameWindowHandle, true);

                if (_windowEventDispatcher != null)
                    _windowEventDispatcher.Invoke(delegate
                    {
                        WinEventHook.WinEventDelegate windowEventDelegate = new WinEventHook.WinEventDelegate(MoveGameWindowEventCallback);
                        _windowEventGCHandle = GCHandle.Alloc(windowEventDelegate);
                        _windowEventHook = WinEventHook.WinEventHookOne(WinEventHook.SWEH_Events.EVENT_OBJECT_LOCATIONCHANGE,
                                                                windowEventDelegate,
                                                                (uint)_gameMemory.Emulator.Id,
                                                                WinEventHook.GetWindowThread(_gameMemory.Emulator.GameWindowHandle));
                    });

                //Get a refernence to the underlying RenderTarget from SharpDX. This'll be used to draw portions of images.
                _device = (SharpDX.Direct2D1.WindowRenderTarget)typeof(Graphics)
                    .GetField("_device", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .GetValue(_graphics);

                _consolas14Bold = _graphics.CreateFont("Consolas", 14, true);
                _consolas16Bold = _graphics.CreateFont("Consolas", 16, true);
                _consolas32Bold = _graphics.CreateFont("Consolas", 32, true);

                _black = _graphics.CreateSolidBrush(0, 0, 0, Config.Opacity);
                _white = _graphics.CreateSolidBrush(255, 255, 255, Config.Opacity);
                _yellow = _graphics.CreateSolidBrush(255, 255, 0, Config.Opacity);
                _green = _graphics.CreateSolidBrush(0, 128, 0, Config.Opacity);
                _lawngreen = _graphics.CreateSolidBrush(124, 252, 0, Config.Opacity);
                _red = _graphics.CreateSolidBrush(255, 0, 0, Config.Opacity);
                _darkred = _graphics.CreateSolidBrush(139, 0, 0, Config.Opacity);
                _grey = _graphics.CreateSolidBrush(128, 128, 128, Config.Opacity);
                _darkergrey = _graphics.CreateSolidBrush(60, 60, 60, Config.Opacity);
                _gold = _graphics.CreateSolidBrush(255, 215, 0, Config.Opacity);
                _goldenrod = _graphics.CreateSolidBrush(218, 165, 32, Config.Opacity);
                _violet = _graphics.CreateSolidBrush(238, 130, 238, Config.Opacity);

                _inventorySheet = ImageLoader.LoadBitmap(_device, Properties.Resources.ICONS);

                _isOverlayReady = true;
            }

            return _isOverlayReady;
        }

        private void UpdateOverlay()
        {
            _window.PlaceAbove(_gameMemory.Emulator.GameWindowHandle);

            if (Config.ScalingFactor != 1f)
                _device.Transform = new SharpDX.Mathematics.Interop.RawMatrix3x2(1f, 0f, 0f, 1f, 0f, 0f);

            _graphics.BeginScene();
            _graphics.ClearScene();

            if (Config.ScalingFactor != 1f)
                _device.Transform = new SharpDX.Mathematics.Interop.RawMatrix3x2(Config.ScalingFactor, 0f, 0f, Config.ScalingFactor, 0f, 0f);
        }

        private void RenderOverlay()
        {
            Point textSize;
            SolidBrush healthBrush;

            if (!_gameMemory.Player.IsAlive)
                healthBrush = _red;
            else if (_gameMemory.Player.IsPoison)
                healthBrush = _violet;
            else if (_gameMemory.Player.IsCautionYellow)
                healthBrush = _gold;
            else if (_gameMemory.Player.IsCautionOrange)
                healthBrush = _goldenrod;
            else if (_gameMemory.Player.IsDanger)
                healthBrush = _red;
            else
                healthBrush = _green;

            int colWidth = 216;

            int alignX = 10;
            int alignY = 10;

            int baseX = alignX;
            int baseY = alignY;

            int offsetX = baseX;
            int offsetY = baseY;

            int xWidth = colWidth;

            int yHeight = 29;
            int yMargin = 10;

            DrawProgressBar(_darkergrey, healthBrush, offsetX, offsetY, xWidth, yHeight, _gameMemory.Player.DisplayHP, _gameMemory.Player.MaximumHP);
            DrawText(_consolas14Bold, _white, offsetX + 5, offsetY + 6, String.Format("{0} - {1}", _gameMemory.Player.HealthMessage, _gameMemory.Player.StatusName));
            
            offsetY += yHeight;

            if (Config.ShowTimer)
            {
                textSize = DrawText(_consolas32Bold, _white, offsetX, offsetY, _gameMemory.IGT.FormattedString);
                offsetY += (int)textSize.Y;
            }
            else
                offsetY += yMargin;

            if (Config.Debug)
            {
                textSize = DrawText(_consolas14Bold, _grey, offsetX, offsetY, String.Format("T: {0:D10}", _gameMemory.IGT.RunningTimer.ToString()));
                offsetY += (int)textSize.Y;

                textSize = DrawText(_consolas14Bold, _grey, offsetX, offsetY, String.Format("C: {0}", _gameMemory.Version.Code));
                offsetY += (int)textSize.Y;

                textSize = DrawText(_consolas14Bold, _grey, offsetX, offsetY, String.Format("P: {0}", _gameMemory.Emulator.ProcessName));
                offsetY += (int)textSize.Y;

                textSize = DrawText(_consolas14Bold, _grey, offsetX, offsetY, String.Format("I: {0}", _gameMemory.Emulator.Id.ToString()));
                offsetY += (int)textSize.Y + yMargin;
            }

            if (Config.ShowStatistics)
            {
                textSize = DrawText(_consolas14Bold, _white, offsetX, offsetY, String.Format("Saves: {0}", _gameMemory.Player.Saves.ToString()));
                offsetY += (int)textSize.Y;

                textSize = DrawText(_consolas14Bold, _white, offsetX, offsetY, String.Format("Retry: {0}", _gameMemory.Player.Retry.ToString()));
                offsetY += (int)textSize.Y;

                textSize = DrawText(_consolas14Bold, _white, offsetX, offsetY, String.Format("F.A.S: {0}", _gameMemory.Player.FAS.ToString()));
                offsetY += (int)textSize.Y + yMargin;
            }

            if (Config.ShowEnemy)
            {
                textSize = DrawText(_consolas16Bold, _red, offsetX, offsetY, Config.ShowBosses ? "Boss HP" : "Enemy HP");
                offsetY += (int)textSize.Y + yMargin;

                for (int i = 0; i < _gameMemory.Enemy.Length; ++i)
                {
                    EnemyEntry entry = _gameMemory.Enemy[i];

                    if (entry.IsEmpty || (Config.ShowBosses && !entry.IsBoss)) continue;

                    int healthX = offsetX;
                    int healthY = offsetY += i > 0 ? yHeight : 0;

                    DrawProgressBar(_darkergrey, _darkred, healthX, healthY, xWidth, yHeight, entry.DisplayHP, entry.MaximumHP);
                    DrawText(_consolas14Bold, _red, healthX + 5, healthY + 6, Config.DebugEnemy ? entry.DebugMessage : entry.HealthMessage);
                }
            }

            offsetX += colWidth + 4;
            offsetY = baseY;

            if (Config.ShowInventory || Config.ShowEquipment)
                DrawInventoryIcon(_gameMemory.Player.Equipment, offsetX, offsetY, true);

            offsetX += ICON_SLOT_WIDTH / 2;

            if (Config.ShowInventory)
                for (int i = 0; i < _gameMemory.Player.Inventory.Length; ++i)
                    DrawInventoryIcon(_gameMemory.Player.Inventory[i], offsetX, offsetY);
        }

        private void DrawInventoryIcon(InventoryEntry entry, int offsetX, int offsetY, bool isEquip = false)
        {
            int width = ICON_SLOT_WIDTH * entry.SlotSize;
            int height = ICON_SLOT_HEIGHT;

            int cellX = offsetX + entry.SlotColumn * ICON_SLOT_WIDTH;
            int cellY = offsetY + entry.SlotRow * ICON_SLOT_HEIGHT;

            int imageX = cellX;
            int imageY = cellY;

            if (isEquip)
                imageX = imageX - width / 2 + (ICON_SLOT_WIDTH / 2) + (ICON_SLOT_WIDTH / 4);

            SharpDX.Mathematics.Interop.RawRectangleF drawRegion = new SharpDX.Mathematics.Interop.RawRectangleF(imageX, imageY, width, height);
            SharpDX.Mathematics.Interop.RawRectangleF imageRegion;

            if (_inventoryToImageTranslation.ContainsKey(entry.Type))
                imageRegion = _inventoryToImageTranslation[entry.Type];
            else
                imageRegion = new SharpDX.Mathematics.Interop.RawRectangleF(0, 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT);

            imageRegion.Right += imageRegion.Left;
            imageRegion.Bottom += imageRegion.Top;

            drawRegion.Right += drawRegion.Left;
            drawRegion.Bottom += drawRegion.Top;

            if (_inventoryToImageTranslation.ContainsKey(entry.Type))
                _device.DrawBitmap(_inventorySheet, drawRegion, (float)Config.Opacity / 255, SharpDX.Direct2D1.BitmapInterpolationMode.Linear, imageRegion);

            if (entry.HasQuantity)
            {
                SolidBrush textBrush;

                if (!entry.IsInfinite && entry.Quantity <= 0)
                    textBrush = _darkred;
                else if (entry.IsFlame)
                    textBrush = _red;
                else if (entry.IsBOW)
                    textBrush = _lawngreen;
                else if (entry.IsAcid)
                    textBrush = _yellow;
                else
                    textBrush = _white;

                Point textSize = _graphics.MeasureString(_consolas14Bold, entry.Quantity.ToString());
                _graphics.DrawText(_consolas14Bold, textBrush, cellX, cellY + height - textSize.Y, entry.IsInfinite ? "∞" : entry.Quantity.ToString());
            }
        }

        private void DrawProgressBar(SolidBrush backBrush, SolidBrush foreBrush, float x, float y, float width, float height, float value, float maximum = 100)
        {
            if (value > maximum)
                maximum = value;

            // Draw FG.
            Rectangle foreRect = new Rectangle(
                x,
                y,
                x + (width * value / maximum),
                y + height
            );

            // Draw BG.
            Rectangle backRect = new Rectangle(
                x + foreRect.Width,
                y,
                x + width,
                y + height
            );

            _graphics.FillRectangle(backBrush, backRect);
            _graphics.FillRectangle(foreBrush, foreRect);
        }

        private Point DrawText(Font font, IBrush brush, float x, float y, string text)
        {
            _graphics.DrawText(font, brush, x, y, text);
            return _graphics.MeasureString(font, text);
        }

        private void GenerateClipping()
        {
            int itemColumnInc;
            int itemRowInc = -1;

            if (_inventoryToImageTranslation == null)
                _inventoryToImageTranslation = new Dictionary<ItemEnumeration, SharpDX.Mathematics.Interop.RawRectangleF>()
                {
                    // Row 1
                    { ItemEnumeration.None, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.RocketLauncher, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 1, ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH * 2, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.AssaultRifle, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 3, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH * 2, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.SniperRifle, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 5, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH * 2, ICON_SLOT_HEIGHT) },

                    // Row 2
                    { ItemEnumeration.Shotgun, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 1), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.HandgunGlock17, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.GrenadeLauncher, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.BowGun, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.CombatKnife, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.Handgun, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 3
                    { ItemEnumeration.CustomHandgun, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.LinearLauncher, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.HandgunBullets, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH *  ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.MagnumBullets, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.MagnumBulletsInsideCase, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.ShotgunShells, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.GrenadeRounds, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.AcidRounds, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 4
                    { ItemEnumeration.FlameRounds, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.BowGunArrows, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.M93RPart, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.FAidSpray, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.GreenHerb, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.RedHerb, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.BlueHerb, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 5
                    { ItemEnumeration.MixedHerb2Green, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.MixedHerbRedGreen, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.MixedHerbBlueGreen, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.MixedHerb2GreenBlue, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.MixedHerb3Green, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.MixedHerbGreenBlueRed, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 6
                    { ItemEnumeration.InkRibbon, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.Magnum, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.GoldLugers, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 2, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH * 2, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.SubMachineGun, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 4, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH * 2, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.BowGunPowder, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 6, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 7
                    { ItemEnumeration.GunPowderArrow, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.BOWGasRounds, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.MGunBullets, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.GasMask, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.RifleBullets, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.DuraluminCaseUnused, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.ARifleBullets, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 8
                    { ItemEnumeration.AlexandersPierce, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.AlexandersJewel, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.AlfredsRing, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.AlfredsJewel, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.LugerReplica, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.FamilyPicture, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.CalicoBullets, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 9
                    { ItemEnumeration.Lockpick, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.GlassEye, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.PianoRoll, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.SteeringWheel, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.CraneKey, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.Lighter, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.EaglePlate, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 10
                    { ItemEnumeration.SidePack, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.MapRoll, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.HawkEmblem, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.QueenAntObject, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.KingAntObject, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.BiohazardCard, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.DuraluminCaseM93RParts, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.DuraluminCaseBowGunPowder, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.DuraluminCaseMagnumRounds, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 11
                    { ItemEnumeration.Detonator, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.ControlLever, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.GoldDragonfly, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.SilverKey, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.GoldKey, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.ArmyProof, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.NavyProof, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 12
                    { ItemEnumeration.AirForceProof, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.KeyWithTag, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.IDCard, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.Map, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.AirportKey, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.EmblemCard, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.SkeletonPicture, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 13
                    { ItemEnumeration.MusicBoxPlate, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.GoldDragonflyNoWings, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.Album, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.Halberd, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.Extinguisher, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.Briefcase, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.PadlockKey, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 14
                    { ItemEnumeration.TG01, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.SpAlloyEmblem, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.ValveHandle, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.OctaValveHandle, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.MachineRoomKey, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.MiningRoomKey, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.BarCodeSticker, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 15
                    { ItemEnumeration.SterileRoomKey, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.DoorKnob, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.BatteryPack, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.HemostaticWire, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.TurnTableKey, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.ChemStorageKey, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.ClementAlpha, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 16
                    { ItemEnumeration.ClementSigma, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.TankObject, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.SpAlloyEmblemUnused, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.ClementMixture, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.RustedSword, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.Hemostatic, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.SecurityCard, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 17
                    { ItemEnumeration.SecurityFile, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.AlexiasChoker, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.AlexiasJewel, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.QueenAntRelief, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.KingAntRelief, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.RedJewel, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.BlueJewel, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 18
                    { ItemEnumeration.Socket, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.SqValveHandle, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.Serum, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.EarthenwareVase, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.PaperWeight, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.SilverDragonflyNoWings, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.SilverDragonfly, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 19
                    { ItemEnumeration.WingObject, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.Crystal, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.GoldDragonfly1Wing, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.GoldDragonfly2Wings, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.GoldDragonfly3Wings, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.File, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.PlantPot, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Row 20
                    { ItemEnumeration.PictureB, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 0), ICON_SLOT_HEIGHT * ++itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.M1P, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH * 2, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.BowGunPowderUnused, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * (itemColumnInc = 3), ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.EnhancedHandgun, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * ++itemColumnInc, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.PlayingManual, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 6, ICON_SLOT_HEIGHT * itemRowInc, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // Shares Icon (Unused Content)
                    { ItemEnumeration.PrisonersDiary, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 4, ICON_SLOT_HEIGHT * 7, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.DirectorsMemo, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 5, ICON_SLOT_HEIGHT * 7, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.Instructions, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 6, ICON_SLOT_HEIGHT * 7, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.AlfredsMemo, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 3, ICON_SLOT_HEIGHT * 15, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.BoardClip, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 6, ICON_SLOT_HEIGHT * 19, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },

                    // No Icon (Unused Content)
                    { ItemEnumeration.Card, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.CrestKeyS, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.CrestKeyG, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.EmptyExtinguisher, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.FileFolders, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.Memo, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.NewspaperClip, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.SquareSocket, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.RemoteController, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.QueenAntReliefComplete, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.QuestionA, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.QuestionB, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.QuestionC, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.QuestionD, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) },
                    { ItemEnumeration.QuestionE, new SharpDX.Mathematics.Interop.RawRectangleF(ICON_SLOT_WIDTH * 0, ICON_SLOT_HEIGHT * 0, ICON_SLOT_WIDTH, ICON_SLOT_HEIGHT) }
                };
        }

        protected void MoveGameWindowEventCallback(IntPtr hWinEventHook,
                                    WinEventHook.SWEH_Events eventType,
                                    IntPtr hWnd,
                                    WinEventHook.SWEH_ObjectId idObject,
                                    long idChild,
                                    uint dwEventThread,
                                    uint dwmsEventTime)
        {
            if (hWnd == _gameMemory.Emulator.GameWindowHandle &&
                eventType == WinEventHook.SWEH_Events.EVENT_OBJECT_LOCATIONCHANGE &&
                idObject == WinEventHook.SWEH_ObjectId.OBJID_WINDOW)
                _window?.FitTo(_gameMemory.Emulator.GameWindowHandle, true);
        }
    }
}