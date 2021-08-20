//*********************************************************
//
// Copyright (c) Microsoft. All rights reserved.
// This code is licensed under the MIT License (MIT).
// THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
// IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
// PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.
//
//*********************************************************

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

using Telerik.Charting;
using Telerik.UI.Xaml.Controls.Chart;
using Telerik.Core;
using System.Collections.Specialized;
using Windows.Foundation;
using Windows.System;
using Windows.Storage;
using System.ComponentModel.Design;

namespace SDKTemplate
{
    // This scenario connects to the device selected in the "Discover
    // GATT Servers" scenario and communicates with it.
    // Note that this scenario is rather artificial because it communicates
    // with an unknown service with unknown characteristics.
    // In practice, your app will be interested in a specific service with
    // a specific characteristic.
    public sealed partial class Scenario2_Client : Page
    {
        private MainPage rootPage = MainPage.Current;

        private BluetoothLEDevice bluetoothLeDevice = null;
        private GattCharacteristic selectedCharacteristic;

        // Only one registered characteristic at a time.
        private GattCharacteristic registeredCharacteristic;
        private GattPresentationFormat presentationFormat;

        private ChartViewModel chartViewModel;

        private DispatcherTimer updateTimer;

        #region Error Codes
        readonly int E_BLUETOOTH_ATT_WRITE_NOT_PERMITTED = unchecked((int)0x80650003);
        readonly int E_BLUETOOTH_ATT_INVALID_PDU = unchecked((int)0x80650004);
        readonly int E_ACCESSDENIED = unchecked((int)0x80070005);
        readonly int E_DEVICE_NOT_AVAILABLE = unchecked((int)0x800710df); // HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_AVAILABLE)
        #endregion

        private readonly object dataFileLock = new object();

        #region UI Code
        public Scenario2_Client()
        {
            InitializeComponent();
        }

        public ObservableChartData chartData
        {
            get
            {
                return chartViewModel.GetData;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            MainPage.clientPage = this;
            SelectedDeviceId.Text = string.Format("({0})", rootPage.SelectedBleDeviceId.Replace("BluetoothLE#BluetoothLE", ""));
            SelectedDeviceName.Text = "Identifying...";
            if (string.IsNullOrEmpty(rootPage.SelectedBleDeviceId))
            {
                ConnectButton.IsEnabled = false;
            }
            else
            {
                if (bluetoothLeDevice == null || bluetoothLeDevice.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                    ConnectButton_Click(); //auto click on page load if we don't have a device yet or we do but it isn't connected
            }

            ClearData();
        }

        public async void ClearData()
        {
            chartViewModel = new ChartViewModel();
            this.twoAxisChart.DataContext = chartViewModel.GetData;
            this.oneAxisChart.DataContext = chartViewModel.GetData;

            //setup initial calibration text.
            UpdateOneAxisCalibrationText();
            UpdateTwoAxisCalibrationText();

            dataLoggingNewFileTime.Items.Clear();
            dataLoggingNewFileTime.Items.Add("1 minute");
            for (int minutes = 2; minutes <= 60; minutes++)
                dataLoggingNewFileTime.Items.Add(string.Format("{0} minutes", minutes.ToString()));
            dataLoggingNewFileTime.SelectedIndex = 0;

            if (updateTimer != null && updateTimer.IsEnabled)
                updateTimer.Stop();

            if (angleCharacteristic != null)
            {
                angleCharacteristic.ValueChanged -= AngleCharacteristic_ValueChanged;
            }

            model = Models.None;

            var success = await ClearBluetoothLEDeviceAsync();
            if (!success)
            {
                rootPage.NotifyUser("Error: Unable to reset app state", NotifyType.ErrorMessage);
            }

            Panel_DataLogging.Visibility = Visibility.Collapsed;

            oneAxisCalibrationStep = 0;
            twoAxisCalibrationStep = 0;

            checkboxOneAxisCalibrationCountdown.IsChecked = calibrateCountdown;
            checkboxTwoAxisCalibrationCountdown.IsChecked = calibrateCountdown;

            forceBendCalibration = false;
            forceStretchCalibration = false;

            HideOneAxisCalibrationInstructionPanel();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
        }
        #endregion

        #region Enumerating Services
        private async Task<bool> ClearBluetoothLEDeviceAsync()
        {
            if (subscribedForNotifications)
            {
                // Need to clear the CCCD from the remote device so we stop receiving notifications
                var result = await registeredCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
                if (result != GattCommunicationStatus.Success)
                {
                    return false;
                }
                else
                {
                    selectedCharacteristic.ValueChanged -= AngleCharacteristic_ValueChanged;
                    subscribedForNotifications = false;
                }
            }
            bluetoothLeDevice?.Dispose();
            bluetoothLeDevice = null;
            return true;
        }

        private async void ConnectButton_Click()
        {
            ConnectButton.IsEnabled = false;
            connectingIndicator.Visibility = Visibility.Visible;

            if (!await ClearBluetoothLEDeviceAsync())
            {
                rootPage.NotifyUser("Error: Unable to reset state, try again.", NotifyType.ErrorMessage);
                ConnectButton.IsEnabled = true;
                return;
            }

            try
            {
                // BT_Code: BluetoothLEDevice.FromIdAsync must be called from a UI thread because it may prompt for consent.
                bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(rootPage.SelectedBleDeviceId);

                if (bluetoothLeDevice == null)
                {
                    rootPage.NotifyUser("Failed to connect to device.", NotifyType.ErrorMessage);
                }
            }
            catch (Exception ex) when (ex.HResult == E_DEVICE_NOT_AVAILABLE)
            {
                rootPage.NotifyUser("Bluetooth radio is not on.", NotifyType.ErrorMessage);
            }

            if (bluetoothLeDevice != null)
            {
                // Note: BluetoothLEDevice.GattServices property will return an empty list for unpaired devices. For all uses we recommend using the GetGattServicesAsync method.
                // BT_Code: GetGattServicesAsync returns a list of all the supported services of the device (even if it's not paired to the system).
                // If the services supported by the device are expected to change during BT usage, subscribe to the GattServicesChanged event.
                GattDeviceServicesResult result = await bluetoothLeDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);

                if (result.Status == GattCommunicationStatus.Success)
                {
                    var serviceList = result.Services;
                    services = new Dictionary<GattNativeServiceUuid, GattDeviceService>();
                    rootPage.NotifyUser(String.Format("Found {0} services", serviceList.Count), NotifyType.StatusMessage);
                    foreach (var service in serviceList)
                    {
                        services.Add(DisplayHelpers.GetServiceUuid(service), service);
                    }
                    ConnectButton.Visibility = Visibility.Collapsed;

                    deviceInfoCharacteristics = new Dictionary<GattNativeCharacteristicUuid, GattCharacteristic>();
                    var infoCharacteristics = await services[GattNativeServiceUuid.DeviceInformation].GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    foreach (var characteristic in infoCharacteristics.Characteristics)
                    {
                        deviceInfoCharacteristics.Add(DisplayHelpers.GetCharacteristicUuid(characteristic), characteristic);
                    }

                    string modelNumber = "Unknown";
                    GattCharacteristic gattCharacteristic;
                    if (deviceInfoCharacteristics.TryGetValue(GattNativeCharacteristicUuid.ModelNumberString, out gattCharacteristic))
                    {
                        var modelNumberValue = await gattCharacteristic.ReadValueAsync();
                        byte[] data;
                        CryptographicBuffer.CopyToByteArray(modelNumberValue.Value, out data);
                        modelNumber = Encoding.UTF8.GetString(data);

                        Debug.WriteLine(modelNumber);
                        if (modelNumber == "ADS_ONE_AXIS")
                        {
                            Panel_ModelSingle.Visibility = Visibility.Visible;
                            Panel_ModelDual.Visibility = Visibility.Collapsed;
                            model = Models.OneAxis;
                            SelectedDeviceName.Text = "One Axis Sensor";
                        }
                        else if (modelNumber == "ADS_TWO_AXIS")
                        {
                            Panel_ModelSingle.Visibility = Visibility.Collapsed;
                            Panel_ModelDual.Visibility = Visibility.Visible;
                            model = Models.TwoAxis;
                            SelectedDeviceName.Text = "Two Axis Sensor";
                        }
                    }
                    Panel_DataLogging.Visibility = Visibility.Visible;
                    rootPage.NotifyUser(String.Format("Found {0} sensor.", modelNumber), NotifyType.StatusMessage);

                    var angleCharacteristics = await services[GattNativeServiceUuid.Angle].GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    if (angleCharacteristics != null && angleCharacteristics.Characteristics.Count > 0)
                    {
                        angleCharacteristic = angleCharacteristics.Characteristics[0];
                        try
                        {
                            // BT_Code: Must write the CCCD in order for server to send indications.
                            // We receive them in the ValueChanged event handler.
                            var status = await angleCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);

                            if (status == GattCommunicationStatus.Success)
                            {
                                updateTimer = new DispatcherTimer();
                                updateTimer.Tick += UpdateTimer_Tick;
                                updateTimer.Interval = new TimeSpan(0, 0, 0, 0, 25);
                                updateTimer.Start();
                                angleCharacteristic.ValueChanged += AngleCharacteristic_ValueChanged;
                                rootPage.NotifyUser("Successfully subscribed for value changes", NotifyType.StatusMessage);
                            }
                            else
                            {
                                rootPage.NotifyUser($"Error registering for value changes: {status}", NotifyType.ErrorMessage);
                            }
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            // This usually happens when a device reports that it support indicate, but it actually doesn't.
                            rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                        }
                    }
                }
                else
                { 
                    rootPage.NotifyUser("Device unreachable", NotifyType.ErrorMessage);
                }
            }
            ConnectButton.IsEnabled = true;
            connectingIndicator.Visibility = Visibility.Collapsed;
        }

        private IOutputStream loggingOutputStream;
        private DataWriter loggingWriteStream;
        private DateTime loggingFileNextSplitTime;
        private StorageFolder loggingStorageFolder;
        private const string loggingDataFormat = "{0:0.00},{1:0.00},{2},=\"{3:h:mm:ss.ffff tt}\"\n";

        private async void CloseLoggingFile()
        {
            if (loggingOutputStream != null)
            {
                await loggingOutputStream.FlushAsync();
                loggingOutputStream.Dispose();
                loggingOutputStream = null;
            }
        }

        private double yAxisMin;
        private double yAxisMax;
        private Size zoomSize = new Size();
        private Point scrollOffset = new Point();
        private async void UpdateTimer_Tick(object sender, object e)
        {
            if (changed == false)
                return;

            //log the data to file(s)
            if (dataLogging)
            {
                //if we have a file open, and we're past the split time, close it
                if (loggingOutputStream != null && DateTime.Now > loggingFileNextSplitTime)
                {
                    CloseLoggingFile();
                }

                //if we don't have a file open, create one and open it
                if (loggingOutputStream == null)
                {
                    string filename = string.Format(loggingFileNameFormat, DateTime.Now);
                    StorageFile loggingFile = await loggingStorageFolder.CreateFileAsync(filename, CreationCollisionOption.GenerateUniqueName);
                    var loggingStream = await loggingFile.OpenAsync(FileAccessMode.ReadWrite);
                    loggingOutputStream = loggingStream.GetOutputStreamAt(loggingStream.Size);
                    loggingWriteStream = new DataWriter(loggingOutputStream);
                    loggingFileNextSplitTime = DateTime.Now + new TimeSpan(0, loggingSplitMinutes, 0);

                    if (model == Models.OneAxis)
                        loggingWriteStream.WriteString("Bend,Stretch,Unix Time,Local Time\n");
                    else
                        loggingWriteStream.WriteString("Bend Horizontal,Bend Vertical,Unix Time,Local Time\n");
                }
            }

            //write the data into the chart
            lock (changedData)
            {
                for (int index = 0; index < changedDataIndex; index++)
                {
                    chartData.RemoveAt(0);

                    chartData.Add(new ChartData() { time = changedData[index].timeStamp, x = changedData[index].value1, y = changedData[index].value2 });

                    if (dataLogging)
                    {
                        //output the data
                        loggingWriteStream.WriteString(string.Format(loggingDataFormat, changedData[index].value1, changedData[index].value2, changedData[index].timeStamp.ToFileTimeUtc(), changedData[index].timeStamp));
                    }
                }

                changedDataIndex = 0;
                changed = false;
            }

            bool shouldScale;
            double chartMaxDegrees = 0;

            if (model == Models.TwoAxis)
            {
                shouldScale = (twoAxisAuto.IsChecked.HasValue && twoAxisAuto.IsChecked.Value);
                chartMaxDegrees = ((LinearAxis)twoAxisChart.VerticalAxis).Maximum;
            }
            else
            {
                shouldScale = (oneAxisAuto.IsChecked.HasValue && oneAxisAuto.IsChecked.Value);
                chartMaxDegrees = ((LinearAxis)oneAxisChart.VerticalAxis).Maximum;
            }

            if (shouldScale)
            {
                double chartTotalDegrees = chartMaxDegrees * 2;
                yAxisMin = chartData[0].x;
                yAxisMax = chartData[0].x;

                bool useYValues = (model == Models.TwoAxis);

                for (int point = 0; point < chartData.Count; point++)
                {
                    if (chartData[point].x < yAxisMin)
                        yAxisMin = chartData[point].x;
                    if (useYValues && chartData[point].y < yAxisMin)
                        yAxisMin = chartData[point].y;

                    if (chartData[point].x > yAxisMax)
                        yAxisMax = chartData[point].x;
                    if (useYValues && chartData[point].y > yAxisMax)
                        yAxisMax = chartData[point].y;
                }

                double range = (yAxisMax - yAxisMin);
                if (range < 10)
                    range = 10;

                double newZoom = (chartTotalDegrees / (range)) - 1.0;

                if (newZoom < 1)
                {
                    zoomSize.Width = 1;
                    zoomSize.Height = 1;
                    scrollOffset.X = 0;
                    scrollOffset.Y = 0;
                }
                else
                {
                    zoomSize.Width = 1;
                    zoomSize.Height = newZoom;

                    //lol math
                    scrollOffset.X = 0;
                    scrollOffset.Y = (chartMaxDegrees - (((yAxisMax - yAxisMin) / 2) + yAxisMin)) / chartTotalDegrees;
                    scrollOffset.Y *= -(newZoom) + 1;
                }

                if (model == Models.TwoAxis)
                {
                    twoAxisChart.Zoom = zoomSize;
                    twoAxisChart.ScrollOffset = scrollOffset;
                }
                else if (model == Models.OneAxis)
                {
                    oneAxisChart.Zoom = zoomSize;
                    oneAxisChart.ScrollOffset = scrollOffset;
                }
            }


            if (dataLogging)
            {
                try
                {
                    //write the actual text to file
                    await loggingWriteStream.StoreAsync();
                }
                catch
                {
                    rootPage.NotifyUser("Error logging to file. Is it open elsewhere?", NotifyType.StatusMessage);
                }
            }

            //update the labels with angle values
            if (model == Models.OneAxis)
            {
                double bend = chartData[chartData.Count - 1].bend;
                double stretch = chartData[chartData.Count - 1].stretch;

                Value_Angle.Text = bend.ToString("000.00");

                if (stretchEnabled)
                {
                    Value_Stretch.Text = stretch.ToString("000.00");

                    if (stretch < 0)
                        stretch = 0;
                    else if (stretch > 35)
                        stretch = 35;

                    oneAxisBulletStretch.FeaturedMeasure = stretch;
                    oneAxisBulletStretch.ComparativeMeasure = stretch;
                }
            }
            else if (model == Models.TwoAxis)
            {
                Value_Horizontal.Text = chartData[chartData.Count - 1].x.ToString("000.00");
                Value_Vertical.Text = chartData[chartData.Count - 1].y.ToString("000.00");
            }

            chartData.NotifyChanged();
        }

        private void ChartAutoScale_Click(object sender, RoutedEventArgs e)
        {
            bool autoScaleEnabled = ((CheckBox)sender).IsChecked.HasValue && ((CheckBox)sender).IsChecked.Value;

            if (autoScaleEnabled)
            {
                if (model == Models.TwoAxis)
                {
                    ((LinearAxis)twoAxisChart.VerticalAxis).Minimum = -2250;
                    ((LinearAxis)twoAxisChart.VerticalAxis).Maximum = 2250;
                    twoAxisChartYAxis.MajorStep = 0;
                }
                else
                {
                    ((LinearAxis)oneAxisChart.VerticalAxis).Minimum = -2250;
                    ((LinearAxis)oneAxisChart.VerticalAxis).Maximum = 2250;
                    oneAxisChartYAxis.MajorStep = 0;
                }
            }
            else
            {
                zoomSize.Width = 1;
                zoomSize.Height = 1;
                scrollOffset.X = 0;
                scrollOffset.Y = 0;

                if (model == Models.TwoAxis)
                {
                    ((LinearAxis)twoAxisChart.VerticalAxis).Minimum = -225;
                    ((LinearAxis)twoAxisChart.VerticalAxis).Maximum = 225;

                    twoAxisChart.Zoom = zoomSize;
                    twoAxisChart.ScrollOffset = scrollOffset;
                    twoAxisChartYAxis.MajorStep = 45;
                }
                else
                {
                    ((LinearAxis)oneAxisChart.VerticalAxis).Minimum = -225;
                    ((LinearAxis)oneAxisChart.VerticalAxis).Maximum = 225;

                    oneAxisChart.Zoom = zoomSize;
                    oneAxisChart.ScrollOffset = scrollOffset;
                    oneAxisChartYAxis.MajorStep = 45;
                }
            }
        }

        private Models model;
        private enum Models
        {
            None,
            OneAxis,
            TwoAxis
        }

        public enum 
            _COMMANDS : byte
        {
            ADS_RUN = 0,            // Place ADS in freerun interrupt mode or standby
            ADS_SPS,                // Update SPS on ADS in freerun interrupt mode
            ADS_RESET,              // Software reset command
            ADS_DFU,                // Reset ADS into bootloader for firmware update
            ADS_SET_ADDRESS,        // Update the I2C address on the ADS
            ADS_POLLED_MODE,        // Place ADS in polled mode or standby
            ADS_GET_FW_VER,         // Get firwmare version on the ADS
            ADS_CALIBRATE,          // Calibration command, see ADS_CALIBRATION_STEP_T
            ADS_READ_STRETCH,       // Enable simultaneous bend and stretch measurements
            ADS_SHUTDOWN,           // Shuts ADS down, lowest power mode, requires reset to wake
            ADS_GET_DEV_ID			// Gets unique device ID for ADS sensor, see ADS_DEV_IDS_T
        }

        private bool calibrateCountdown = true;
        private void Checkbox_Calibrate_Countdown_Checked(object sender, RoutedEventArgs e)
        {
            if (model == Models.OneAxis)
            {
                calibrateCountdown = checkboxOneAxisCalibrationCountdown.IsChecked.Value;
                UpdateOneAxisCalibrationText();
            }
            else
            {
                calibrateCountdown = checkboxTwoAxisCalibrationCountdown.IsChecked.Value;
                UpdateTwoAxisCalibrationText();
            }
        }

        private bool stretchEnabled = false;

        private async void Checkbox_Stretch_Checked(object sender, RoutedEventArgs e)
        {
            bool enable = Check_Stretch.IsChecked.Value;
            stretchEnabled = enable;

            var writer = new DataWriter();
            writer.ByteOrder = ByteOrder.LittleEndian;
            writer.WriteByte(enable ? (byte)1 : (byte)0);
            writer.WriteByte((byte)0x80); //todo: why doesn't the enum work?

            var writeOperation = await angleCharacteristic.WriteValueAsync(writer.DetachBuffer());

            if (enable)
            {
                rootPage.NotifyUser("Stretch mode enabled: " + writeOperation.ToString(), NotifyType.StatusMessage);
                //oneAxisChartStretch.Visibility = Visibility.Visible;
                Label_Stretch.Opacity = 1;
                Value_Stretch.Opacity = 1;
                oneAxisBulletStretch.Visibility = Visibility.Visible;
            }
            else
            {
                rootPage.NotifyUser("Stretch mode disabled", NotifyType.StatusMessage);
                //oneAxisChartStretch.Visibility = Visibility.Collapsed;
                Label_Stretch.Opacity = 0.5;
                Value_Stretch.Opacity = 0.5;
                oneAxisBulletStretch.Visibility = Visibility.Collapsed;
            }

            UpdateOneAxisCalibrationText();
        }

        bool changed = false;
        struct BleData
        {
            public float value1, value2;
            public DateTime timeStamp;
        }
        BleData[] changedData = new BleData[300];
        int changedDataIndex = 0;

        private async void AngleCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            byte[] data;
            CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out data);
            if (model == Models.None)
            {
                if (data.Length == 4)
                {
                    model = Models.OneAxis;

                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        Panel_ModelSingle.Visibility = Visibility.Visible;
                        Panel_ModelDual.Visibility = Visibility.Collapsed;
                        SelectedDeviceName.Text = "One Axis Sensor";
                    });
                }
                if (data.Length == 8)
                {
                    model = Models.TwoAxis;

                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        Panel_ModelSingle.Visibility = Visibility.Collapsed;
                        Panel_ModelDual.Visibility = Visibility.Visible;
                        SelectedDeviceName.Text = "Two Axis Sensor";
                    });
                }
            }
            if (model == Models.None)
                return;

            float value1 = BitConverter.ToSingle(data, 0);
            float value2 = BitConverter.ToSingle(data, 4);
            DateTime timestamp = DateTime.Now;// args.Timestamp.DateTime;

            lock (changedData)
            {
                changedData[changedDataIndex].value1 = value1;
                changedData[changedDataIndex].value2 = value2;
                changedData[changedDataIndex].timeStamp = timestamp;
                changedDataIndex++;
                if (changedDataIndex >= changedData.Length)
                    changedDataIndex = 0;
                changed = true;
            }
        }
        #endregion

        private Dictionary<GattNativeServiceUuid, GattDeviceService> services;
        private Dictionary<GattNativeCharacteristicUuid, GattCharacteristic> deviceInfoCharacteristics;
        private GattCharacteristic angleCharacteristic;

        private int oneAxisCalibrationStep = 0;
        private async void OneAxisCalibrate_Click()
        {
            if (calibrateCountdown && oneAxisCalibrationStep < 2)
            {
                oneAxisCalibrationButton.IsEnabled = false;
                for (int calibrateCount = secondsToCalibrate; calibrateCount > 0; calibrateCount--)
                {
                    oneAxisCalibrationButton.Content = string.Format("({0})", calibrateCount);
                    await Task.Delay(1000);
                }
                oneAxisCalibrationButton.IsEnabled = true;
            }

            if (calibrationType == CalibrationTypes.Bend)
            {
                switch (oneAxisCalibrationStep)
                {
                    case 0: //flat
                        var flat = await WriteAngleCalibrationStep(0);
                        rootPage.NotifyUser(string.Format("Flat calibration result: {0}", flat.ToString()), NotifyType.StatusMessage);
                        break;
                    case 1: //90
                        var perp = await WriteAngleCalibrationStep(1);
                        rootPage.NotifyUser(string.Format("90° calibration result: {0}", perp.ToString()), NotifyType.StatusMessage);

                        forceBendCalibration = false; //completed
                        break;
                    case 2:
                        if (forceStretchCalibration)
                        {
                            calibrationType = CalibrationTypes.Stretch;
                            oneAxisCalibrationStep = -1;
                            forceStretchCalibration = false;
                        }
                        break;
                }
            }
            else if (calibrationType == CalibrationTypes.Stretch)
            {
                switch (oneAxisCalibrationStep)
                {
                    case 0: //no stretch
                        var noStretch = await WriteAngleCalibrationStep(4);
                        rootPage.NotifyUser(string.Format("Zero stretch calibration result: {0}", noStretch.ToString()), NotifyType.StatusMessage);
                        break;
                    case 1: //30mm
                        var stretch = await WriteAngleCalibrationStep(5);
                        rootPage.NotifyUser(string.Format("30mm stretch calibration result: {0}", stretch.ToString()), NotifyType.StatusMessage);

                        forceStretchCalibration = false; //completed
                        break;
                    case 2:
                        //just update text to success
                        if (forceBendCalibration)
                        {
                            calibrationType = CalibrationTypes.Bend;
                            oneAxisCalibrationStep = -1;
                            forceBendCalibration = false;
                        }
                        break;
                }
            }

            oneAxisCalibrationStep++;

            if (oneAxisCalibrationStep >= 3)
            {
                oneAxisCalibrationStep = 0;
                calibrationType = CalibrationTypes.None;
            }

            UpdateOneAxisCalibrationText();
        }

        private int twoAxisCalibrationStep = 0;
        private async void TwoAxisCalibrate_Click()
        {
            if (calibrateCountdown && twoAxisCalibrationStep > 0 && twoAxisCalibrationStep < 4)
            {
                twoAxisCalibrationButton.IsEnabled = false;
                for (int calibrateCount = secondsToCalibrate; calibrateCount > 0; calibrateCount--)
                {
                    twoAxisCalibrationButton.Content = string.Format("({0})", calibrateCount);
                    await Task.Delay(1000);
                }
                twoAxisCalibrationButton.IsEnabled = true;
            }

            switch (twoAxisCalibrationStep)
            {
                case 0:
                    //just update text to instructions
                    break;
                case 1: //flat
                    var flat = await WriteAngleCalibrationStep(0);
                    rootPage.NotifyUser(string.Format("Flat calibration result: {0}", flat.ToString()), NotifyType.StatusMessage);
                    break;
                case 2: //90 vertical
                    var vertical = await WriteAngleCalibrationStep(2);
                    rootPage.NotifyUser(string.Format("Vertical 90° calibration result: {0}", vertical.ToString()), NotifyType.StatusMessage);
                    break;
                case 3: //90 horizontal
                    var horizontal = await WriteAngleCalibrationStep(1);
                    rootPage.NotifyUser(string.Format("Horizontal 90° calibration result: {0}", horizontal.ToString()), NotifyType.StatusMessage);
                    break;
                case 4:
                    //just update text to success
                    break;
            }

            twoAxisCalibrationStep++;
            if (twoAxisCalibrationStep >= 5)
                twoAxisCalibrationStep = 0;

            UpdateTwoAxisCalibrationText();
        }

        private int secondsToCalibrate = 5;

        private void UpdateOneAxisCalibrationText()
        {
            if (calibrationType == CalibrationTypes.None)
            {
                HideOneAxisCalibrationInstructionPanel();
            }
            else if (calibrationType == CalibrationTypes.Bend)
            {
                switch (oneAxisCalibrationStep)
                {
                    case 0:
                        oneAxisCalibrationHeader.Text = "Flat";
                        oneAxisCalibrationText.Text = "Place the sensor as flat as it will go. There should be no bend in it at all. ";

                        if (calibrateCountdown)
                            oneAxisCalibrationText.Text += "\nAfter pressing calibrate you'll have " + secondsToCalibrate + " seconds to get into position.";
                        else
                            oneAxisCalibrationText.Text += "Then press the Calibrate button.";

                        oneAxisCalibrationButton.Content = "Calibrate";
                        break;
                    case 1:
                        oneAxisCalibrationHeader.Text = "Bend Perpendicular";
                        oneAxisCalibrationText.Text = "Bend the sensor to a 90 degree angle so it is perpendicular to the pcb in the upward direction ";

                        if (calibrateCountdown)
                            oneAxisCalibrationText.Text += "\nAfter pressing calibrate you'll have " + secondsToCalibrate + " seconds to get into position.";
                        else
                            oneAxisCalibrationText.Text += "With the sensor bent 90° press the Calibrate button.";

                        oneAxisCalibrationButton.Content = "Calibrate";
                        break;
                    case 2:
                        oneAxisCalibrationHeader.Text = "Complete!";

                        if (forceStretchCalibration)
                        {
                            oneAxisCalibrationText.Text = "The bend calibration process is complete! Now let's calibrate Stretch.";
                        }
                        else
                        {
                            oneAxisCalibrationText.Text = "The calibration process is complete! You should see the new calibration reflected in the values immediately.";
                        }
                        oneAxisCalibrationButton.Content = "Ok";
                        break;
                }

                ShowOneAxisCalibrationInstructionPanel();
            }
            else if (calibrationType == CalibrationTypes.Stretch)
            {
                if (stretchEnabled == false)
                {
                    Check_Stretch.IsChecked = true; //this will turn on stretch mode
                    Checkbox_Stretch_Checked(null, null);
                }

                switch (oneAxisCalibrationStep)
                {
                    case 0:
                        oneAxisCalibrationHeader.Text = "No stretch";
                        oneAxisCalibrationText.Text = "Flatten out the sensor so there is no bend and you're not stretching it. ";

                        if (calibrateCountdown)
                            oneAxisCalibrationText.Text += "\nAfter pressing calibrate you'll have " + secondsToCalibrate + " seconds to get into position.";
                        else
                            oneAxisCalibrationText.Text += "Then press the Calibrate button.";

                        oneAxisCalibrationButton.Content = "Calibrate";
                        break;
                    case 1:
                        oneAxisCalibrationHeader.Text = "30mm stretch";
                        oneAxisCalibrationText.Text = "Stretch the sensor by 30mm. ";

                        if (calibrateCountdown)
                            oneAxisCalibrationText.Text += "\nAfter pressing calibrate you'll have " + secondsToCalibrate + " seconds to get into position."; 
                        else
                            oneAxisCalibrationText.Text += "While it is extended press the Calibrate button.";

                        oneAxisCalibrationButton.Content = "Calibrate";
                        break;
                    case 2:
                        oneAxisCalibrationHeader.Text = "Complete!";

                        if (forceBendCalibration)
                        {
                            oneAxisCalibrationText.Text = "The stretch calibration process is complete! Now let's calibrate Bend.";
                        }
                        else
                        {
                            oneAxisCalibrationText.Text = "The calibration process is complete! You should see the new calibration reflected in the values immediately.";
                        }

                        oneAxisCalibrationButton.Content = "Ok";
                        break;
                }

                ShowOneAxisCalibrationInstructionPanel();
            }
        }

        private void ShowOneAxisCalibrationInstructionPanel()
        {
            oneAxisCalibrationChoosePanel.Visibility = Visibility.Collapsed;
            oneAxisCalibrationExecutePanel.Visibility = Visibility.Visible;
        }

        private void HideOneAxisCalibrationInstructionPanel()
        {
            oneAxisCalibrationChoosePanel.Visibility = Visibility.Visible;
            oneAxisCalibrationExecutePanel.Visibility = Visibility.Collapsed;

            if (forceBendCalibration || forceStretchCalibration)
            {
                calibrateBend.Visibility = Visibility.Collapsed;
                calibrateStretch.Visibility = Visibility.Collapsed;
                calibrateBendStretch.Visibility = Visibility.Visible;
            }
            else
            {
                calibrateBend.Visibility = Visibility.Visible;
                calibrateStretch.Visibility = Visibility.Visible;
                calibrateBendStretch.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateTwoAxisCalibrationText()
        {
            switch (twoAxisCalibrationStep)
            {
                case 0:
                    twoAxisCalibrationHeader.Text = "3 step process";
                    twoAxisCalibrationText.Text = "This will lead you through a three step calibration process.";
                    twoAxisCalibrationButton.Content = "Begin";
                    break;
                case 1:
                    twoAxisCalibrationHeader.Text = "Flat";
                    twoAxisCalibrationText.Text = "Place the sensor as flat as it will go on a flat surface. There should be no bend in the sensor in either direction. ";

                    if (calibrateCountdown)
                        twoAxisCalibrationText.Text += "\nAfter pressing calibrate you'll have " + secondsToCalibrate + " seconds to get into position.";
                    else
                        twoAxisCalibrationText.Text += "Then press the Calibrate button.";

                    twoAxisCalibrationButton.Content = "Calibrate";
                    break;
                case 2:
                    twoAxisCalibrationHeader.Text = "Bend 90° Vertically";
                    twoAxisCalibrationText.Text = "Bend the sensor to a 90 degree angle vertically so the sensor tip is perpendicular to the pcb. If the pcb is flat on a table the sensor tip should point in the upward direction. ";

                    if (calibrateCountdown)
                        twoAxisCalibrationText.Text += "\nAfter pressing calibrate you'll have " + secondsToCalibrate + " seconds to get into position.";
                    else
                        twoAxisCalibrationText.Text += "With the sensor bent 90° press the Calibrate button.";

                    twoAxisCalibrationButton.Content = "Calibrate";
                    break;
                case 3:
                    twoAxisCalibrationHeader.Text = "Bend 90° Horizontally";
                    twoAxisCalibrationText.Text = "Release the sensor. Now bend the sensor to a 90° angle horizontally so it is perpendicular to the pcb. For example, if the sensor is parallel with your keyboard, and pointing to the right, you should bend it towards your body (away from the keyboard). ";

                    if (calibrateCountdown)
                        twoAxisCalibrationText.Text += "\nAfter pressing calibrate you'll have " + secondsToCalibrate + " seconds to get into position.";
                    else
                        twoAxisCalibrationText.Text += "With the sensor bent 90° press the Calibrate button.";

                    twoAxisCalibrationButton.Content = "Calibrate";
                    break;
                case 4:
                    twoAxisCalibrationHeader.Text = "Complete!";
                    twoAxisCalibrationText.Text = "The calibration process is complete! You should see the new calibration reflected in the values and plot immediately.";
                    twoAxisCalibrationButton.Content = "Ok";
                    break;
            }

            if (twoAxisCalibrationStep == 0 || twoAxisCalibrationStep == 4)
                twoAxisCalibrationClearButton.Visibility = Visibility.Visible;
            else
                twoAxisCalibrationClearButton.Visibility = Visibility.Collapsed;
        }
        private void CalibrationBend_Click()
        {
            calibrationType = CalibrationTypes.Bend;
            UpdateOneAxisCalibrationText();
        }

        private void CalibrationStretch_Click()
        {
            calibrationType = CalibrationTypes.Stretch;
            UpdateOneAxisCalibrationText();
        }

        private CalibrationTypes calibrationType;
        private enum CalibrationTypes
        {
            None,
            Bend,
            Stretch,
        }

        private bool forceStretchCalibration = false;
        private bool forceBendCalibration = false;

        private async void CalibrationClear_Click()
        {
            var clear = await WriteAngleCalibrationStep(3);
            rootPage.NotifyUser(string.Format("Clear calibration result: {0}", clear.ToString()), NotifyType.StatusMessage);

            oneAxisCalibrationStep = 0;
            UpdateOneAxisCalibrationText();

            twoAxisCalibrationStep = 0;
            UpdateTwoAxisCalibrationText();

            if (model == Models.OneAxis && stretchEnabled == true)
            {
                //then we must calibrate stretch.
                forceBendCalibration = true;
                forceStretchCalibration = true; 
                HideOneAxisCalibrationInstructionPanel();
            }
        }

        private IAsyncOperation<GattCommunicationStatus> WriteAngleCalibrationStep(int data)
        {
            var writer = new DataWriter();
            writer.ByteOrder = ByteOrder.LittleEndian;
            writer.WriteByte((byte)data);

            return angleCharacteristic.WriteValueAsync(writer.DetachBuffer());
        }

        private void SetVisibility(UIElement element, bool visible)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool dataLogging = false;
        private string loggingFileNameFormat;
        private int loggingSplitMinutes;

        private const string loggingFileNameTemplate = "{0:MM-dd-yyyy_HH-mm-ss}";
        private async void DataLoggingStart_Click()
        {
            if (!dataLogging) //start logging
            {
                dataLoggingFileName.IsEnabled = false;
                dataLoggingNewFileTime.IsEnabled = false;

                if (string.IsNullOrEmpty(dataLoggingFileName.Text))
                    loggingFileNameFormat = string.Format("{1}.csv", dataLoggingFileName.Text, loggingFileNameTemplate);
                else
                    loggingFileNameFormat = string.Format("{0}_{1}.csv", dataLoggingFileName.Text, loggingFileNameTemplate);
                
                string timeString = dataLoggingNewFileTime.SelectedValue.ToString();
                timeString = timeString.Substring(0, timeString.IndexOf(' '));
                loggingSplitMinutes = int.Parse(timeString);

                loggingFileNextSplitTime = DateTime.Now + new TimeSpan(0, loggingSplitMinutes, 0);
                try
                {
                    loggingStorageFolder = await GetDataFolder();

                    dataLoggingStartButton.Content = "Stop";
                    dataLogging = true; //do this at the end so everything is setup before we start

                    rootPage.NotifyUser("Data logging started...", NotifyType.StatusMessage);
                }
                catch (Exception)
                {
                    rootPage.NotifyUser("Failed to create data logging directory.", NotifyType.ErrorMessage);
                }
            }
            else //stop logging
            {
                dataLogging = false; //do this at the beginning so we stop immediately

                dataLoggingStartButton.Content = "Start";
                dataLoggingFileName.IsEnabled = true;
                dataLoggingNewFileTime.IsEnabled = true;

                CloseLoggingFile(); //close the current file

                rootPage.NotifyUser("Data logging stopped.", NotifyType.StatusMessage);
            }
        }

        private async void DataDirectory_Click()
        {
            StorageFolder dataFolder = await GetDataFolder();
            await Launcher.LaunchFolderAsync(dataFolder);
        }

        private async Task<StorageFolder> GetDataFolder()
        {
            try
            {
                StorageFolder documentsFolder = KnownFolders.DocumentsLibrary;
                var outputFolder = await documentsFolder.CreateFolderAsync("BendLabs", CreationCollisionOption.OpenIfExists);
                dataLoggingFolderLink.Content = outputFolder.Name;
                return outputFolder;
            }
            catch (Exception)
            {}
            try
            {
                StorageFolder documentsFolder = ApplicationData.Current.LocalFolder;
                var outputFolder = await documentsFolder.CreateFolderAsync("BendLabs", CreationCollisionOption.OpenIfExists);
                dataLoggingFolderLink.Content = outputFolder.Name;
                return outputFolder;
            }
            catch (Exception)
            {}
            throw new InvalidOperationException("Unable to create data folder");
        }

        private async void CharacteristicReadButton_Click()
        {
            // BT_Code: Read the actual value from the device by using Uncached.
            GattReadResult result = await selectedCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (result.Status == GattCommunicationStatus.Success)
            {
                string formattedResult = FormatValueByPresentation(result.Value, presentationFormat);
                rootPage.NotifyUser($"Read result: {formattedResult}", NotifyType.StatusMessage);
            }
            else
            {
                rootPage.NotifyUser($"Read failed: {result.Status}", NotifyType.ErrorMessage);
            }
        }

        private async void CharacteristicWriteButton_Click()
        {
            if (!String.IsNullOrEmpty(CharacteristicWriteValue.Text))
            {
                var writeBuffer = CryptographicBuffer.ConvertStringToBinary(CharacteristicWriteValue.Text,
                    BinaryStringEncoding.Utf8);

                var writeSuccessful = await WriteBufferToSelectedCharacteristicAsync(writeBuffer);
            }
            else
            {
                rootPage.NotifyUser("No data to write to device", NotifyType.ErrorMessage);
            }
        }

        private async void CharacteristicWriteButtonInt_Click()
        {
            if (!String.IsNullOrEmpty(CharacteristicWriteValue.Text))
            {
                var isValidValue = Int32.TryParse(CharacteristicWriteValue.Text, out int readValue);
                if (isValidValue)
                {
                    var writer = new DataWriter();
                    writer.ByteOrder = ByteOrder.LittleEndian;
                    writer.WriteInt32(readValue);

                    var writeSuccessful = await WriteBufferToSelectedCharacteristicAsync(writer.DetachBuffer());
                }
                else
                {
                    rootPage.NotifyUser("Data to write has to be an int32", NotifyType.ErrorMessage);
                }
            }
            else
            {
                rootPage.NotifyUser("No data to write to device", NotifyType.ErrorMessage);
            }
        }

        private async Task<bool> WriteBufferToSelectedCharacteristicAsync(IBuffer buffer)
        {
            try
            {
                // BT_Code: Writes the value from the buffer to the characteristic.
                var result = await selectedCharacteristic.WriteValueWithResultAsync(buffer);

                if (result.Status == GattCommunicationStatus.Success)
                {
                    rootPage.NotifyUser("Successfully wrote value to device", NotifyType.StatusMessage);
                    return true;
                }
                else
                {
                    rootPage.NotifyUser($"Write failed: {result.Status}", NotifyType.ErrorMessage);
                    return false;
                }
            }
            catch (Exception ex) when (ex.HResult == E_BLUETOOTH_ATT_INVALID_PDU)
            {
                rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                return false;
            }
            catch (Exception ex) when (ex.HResult == E_BLUETOOTH_ATT_WRITE_NOT_PERMITTED || ex.HResult == E_ACCESSDENIED)
            {
                // This usually happens when a device reports that it support writing, but it actually doesn't.
                rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                return false;
            }
        }

        private bool subscribedForNotifications = false;

        private string FormatValueByPresentation(IBuffer buffer, GattPresentationFormat format)
        {
            // BT_Code: For the purpose of this sample, this function converts only UInt32 and
            // UTF-8 buffers to readable text. It can be extended to support other formats if your app needs them.
            byte[] data;
            CryptographicBuffer.CopyToByteArray(buffer, out data);
            if (format != null)
            {
                if (format.FormatType == GattPresentationFormatTypes.UInt32 && data.Length >= 4)
                {
                    return BitConverter.ToInt32(data, 0).ToString();
                }
                else if (format.FormatType == GattPresentationFormatTypes.Utf8)
                {
                    try
                    {
                        return Encoding.UTF8.GetString(data);
                    }
                    catch (ArgumentException)
                    {
                        return "(error: Invalid UTF-8 string)";
                    }
                }
                else
                {
                    // Add support for other format types as needed.
                    return "Unsupported format: " + CryptographicBuffer.EncodeToHexString(buffer);
                }
            }
            else if (data != null)
            {
                // We don't know what format to use. Let's try some well-known profiles, or default back to UTF-8.
                if (selectedCharacteristic.Uuid.Equals(GattCharacteristicUuids.HeartRateMeasurement))
                {
                    try
                    {
                        return "Heart Rate: " + ParseHeartRateValue(data).ToString();
                    }
                    catch (ArgumentException)
                    {
                        return "Heart Rate: (unable to parse)";
                    }
                }
                else if (selectedCharacteristic.Uuid.Equals(GattCharacteristicUuids.BatteryLevel))
                {
                    try
                    {
                        // battery level is encoded as a percentage value in the first byte according to
                        // https://www.bluetooth.com/specifications/gatt/viewer?attributeXmlFile=org.bluetooth.characteristic.battery_level.xml
                        return "Battery Level: " + data[0].ToString() + "%";
                    }
                    catch (ArgumentException)
                    {
                        return "Battery Level: (unable to parse)";
                    }
                }
                // This is our custom calc service Result UUID. Format it like an Int
                else if (selectedCharacteristic.Uuid.Equals(Constants.ResultCharacteristicUuid))
                {
                    return BitConverter.ToInt32(data, 0).ToString();
                }
                // No guarantees on if a characteristic is registered for notifications.
                else if (registeredCharacteristic != null)
                {
                    // This is our custom calc service Result UUID. Format it like an Int
                    if (registeredCharacteristic.Uuid.Equals(Constants.ResultCharacteristicUuid))
                    {
                        return BitConverter.ToInt32(data, 0).ToString();
                    }
                }
                else
                {
                    try
                    {
                        //return BitConverter.ToSingle(data, 0).ToString() + ", " + BitConverter.ToSingle(data, 4).ToString();
                        return "Unknown format: " + Encoding.UTF8.GetString(data);
                    }
                    catch (ArgumentException)
                    {
                        return "Unknown format";
                    }
                }
            }
            else
            {
                return "Empty data received";
            }


            try
            {
                return BitConverter.ToSingle(data, 0).ToString("0.0") + ", " + BitConverter.ToSingle(data, 4).ToString("0.0");
                //return "Unknown format: " + Encoding.UTF8.GetString(data);
            }
            catch (ArgumentException)
            {
                return "Unknown format";
            }
        }

        /// <summary>
        /// Process the raw data received from the device into application usable data,
        /// according the the Bluetooth Heart Rate Profile.
        /// https://www.bluetooth.com/specifications/gatt/viewer?attributeXmlFile=org.bluetooth.characteristic.heart_rate_measurement.xml&u=org.bluetooth.characteristic.heart_rate_measurement.xml
        /// This function throws an exception if the data cannot be parsed.
        /// </summary>
        /// <param name="data">Raw data received from the heart rate monitor.</param>
        /// <returns>The heart rate measurement value.</returns>
        private static ushort ParseHeartRateValue(byte[] data)
        {
            // Heart Rate profile defined flag values
            const byte heartRateValueFormat = 0x01;

            byte flags = data[0];
            bool isHeartRateValueSizeLong = ((flags & heartRateValueFormat) != 0);

            if (isHeartRateValueSizeLong)
            {
                return BitConverter.ToUInt16(data, 1);
            }
            else
            {
                return data[1];
            }
        }
    }

    public class ChartViewModel
    {
        public ChartViewModel()
        {
            for (int index = 0; index < 300; index++)
                Data.Add(new ChartData() { time = DateTime.Now - new TimeSpan(0, 0, 0, 0, index * 10), x = 0, y = 0 });
        }

        public int DataCount => Data.Count;

        public ObservableChartData Data = new ObservableChartData();
        public ObservableChartData GetData
        {
            get
            {
                return Data;
            }
            set
            {
                Data = value;
            }
        }
    }

    public class ChartData
    {
        public double x { get; set; }
        public double y { get; set; }

        public double bend => x;
        public double stretch => y;

        public DateTime time { get; set; }
    }

    public class ObservableChartData : ObservableCollection<ChartData>
    {
        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            //don't notify if it's being done automatically. Wait for the explicit call.
        }

        public void NotifyChanged()
        {
            base.OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }
    }
}

