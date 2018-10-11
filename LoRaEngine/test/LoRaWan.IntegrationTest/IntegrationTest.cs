﻿using System;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Xunit;

namespace LoRaWan.IntegrationTest
{
    /// <summary>
    /// Integration test
    /// </summary>
    public class IntegrationTest : IClassFixture<IntegrationTestFixture>, IDisposable
    {
        private readonly IntegrationTestFixture testFixture;

        public IntegrationTest(IntegrationTestFixture testFixture)
        {
            this.testFixture = testFixture;
        }

        public static string LastLine { get; set; }

        private SerialPort SerialPortWin { get; set; }
        private SerialDevice SerialPort { get; set; }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            this.SerialPort?.Close();
            this.SerialPortWin?.Close();

            this.SerialPort = null;
            this.SerialPortWin = null;
        }

        [Fact]
        public async Task Test_OTAA_Confirmed_And_Unconfirmed_Message()
        {
            
            var leafDeviceLog = new ConcurrentQueue<string>();
            string buff = "";
            LoRaWanClass lora;

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine($"Starting serial port on non-Windows, {this.testFixture}");

                this.SerialPort = new SerialDevice(testFixture.Configuration.LeafDeviceSerialPort, BaudRate.B1152000);
                this.SerialPort.DataReceived += (object sender, byte[] data) => {                    
                    var dataread = System.Text.Encoding.UTF8.GetString(data);
                    dataread.Replace("\r", "");
                    if (dataread.IndexOf("\n") > 0)
                    {
                        LastLine = dataread.Substring(0, dataread.IndexOf("\n"));
                        leafDeviceLog.Enqueue(LastLine);
                        Console.WriteLine("[Serial]" + LastLine);
                        buff = dataread.Substring(dataread.IndexOf("\n") + 1);
                    }
                    else
                        buff += dataread;
                };

                Console.WriteLine($"Opening serial port");
                this.SerialPort.Open();
                lora = new LoRaWanClass(SerialPort);
            }
            else
            {
                Console.WriteLine($"Starting serial port on Windows, {this.testFixture.Configuration.LeafDeviceSerialPort}");

                this.SerialPortWin = new SerialPort(this.testFixture.Configuration.LeafDeviceSerialPort)
                {
                    BaudRate = 115200,
                    Parity = Parity.None,
                    StopBits = StopBits.One,
                    DataBits = 8,
                    DtrEnable = true,
                    Handshake = Handshake.None
                };
                this.SerialPortWin.DataReceived += (object sender, SerialDataReceivedEventArgs e) =>
                {
                    var myserialPort = (SerialPort)sender;

                    var serialdata = myserialPort.ReadLine();
                    if (serialdata.EndsWith('\r'))
                        serialdata = serialdata.Substring(0, serialdata.Length - 1);
                    LastLine = serialdata;
                    leafDeviceLog.Enqueue(LastLine);
                    Console.WriteLine("[Serial]" + LastLine);
                };
                Console.WriteLine($"Opening serial port");
                SerialPortWin.Open();
                lora = new LoRaWanClass(SerialPortWin);
            }

          
            

            // Now open the port.            
            string appSKey = null;
            string nwkSKey = null;
            string devAddr = null;
            var deviceId = testFixture.Configuration.LeafDeviceId;
                       
            lora
                .setDeciveMode(LoRaWanClass._device_mode_t.LWOTAA)
                .setId(devAddr, testFixture.Configuration.LeafDeviceId, testFixture.Configuration.LeafDeviceAppEui)
                .setKey(nwkSKey, appSKey, testFixture.Configuration.LeafDeviceAppKey);

            // EU: lora.setDataRate(LoRaWanClass._data_rate_t.DR6, LoRaWanClass._physical_type_t.EU868);
            //lora.setChannel(0, 868.1F);
            //lora.setChannel(1, 868.3F);
            //lora.setChannel(2, 868.5F);
            //lora.setReceiceWindowFirst(0, 868.1F);
            //lora.setReceiceWindowSecond(868.5F, LoRaWanClass._data_rate_t.DR2);


            lora.setDataRate(LoRaWanClass._data_rate_t.DR0, LoRaWanClass._physical_type_t.US915HYBRID);


            lora.setAdaptiveDataRate(false)
                .setDutyCycle(false)
                .setJoinDutyCycle(false)
                .setPower(14);

            var joinSucceeded = false;

            for (var joinAttempt=1; joinAttempt <= 5; ++joinAttempt)
            {
                Console.WriteLine($"Join attempt #{joinAttempt++}");
                joinSucceeded = lora.setOTAAJoin(LoRaWanClass._otaa_join_cmd_t.JOIN, 20000);
                if (joinSucceeded)
                    break;
            }

            if (!joinSucceeded)
            {
                this.SerialPort?.Close();
                this.SerialPortWin?.Close();
                Assert.True(joinSucceeded, "Join failed");
            }


            // After join: Expectation on serial
            // +JOIN: Network joined
            // +JOIN: NetID 010000 DevAddr 02:9B:0D:3E  
            Assert.Contains("+JOIN: Network joined", leafDeviceLog);
            Assert.Contains(leafDeviceLog, (s) => s.StartsWith("+JOIN: NetID 010000"));


            // TODO: Check with Mikhail why the device twin is not being saved
            // After join: Expectation on device twin
            // DevAddr 02:9B:0D:3E -> exists in device twin reported properties
            //var devAddressInformation = serialContent.Split(System.Environment.NewLine).FirstOrDefault(x => x.StartsWith("+JOIN: NetID 010000 DevAddr"));
            //var devAddress = string.Empty;
            //if (!string.IsNullOrEmpty(devAddressInformation))
            //{
            //    devAddress = devAddressInformation.Replace("+JOIN: NetID 010000 DevAddr", string.Empty).Trim();
            //}
            //var deviceTwin = await this.eventHubDataCollectorFixture.GetTwinAsync(deviceId);
          //  Assert.True(deviceTwin.Properties.Reported.Contains("devAddr"));

            Console.WriteLine("Join succeeded");

            leafDeviceLog.Clear();
            testFixture.Events.ResetEvents();

            lora.transferPacket("100", 10);

            // After transferPacket: Expectation from serial
            // +MSG: Done            
            Assert.Contains("+MSG: Done", leafDeviceLog);            

            // After transferPacket: Expectation from Log
            // 72AAC86800430020: valid frame counter, msg: 1 server: 0
            // 72AAC86800430020: decoding with: DecoderTemperatureSensor port: 1
            // 72AAC86800430020: sent message '{"temperature": 100}' to hub
            Assert.True(
                await testFixture.EnsureHasEvent((e, deviceIdFromMessage, messageBody) => e.Properties.ContainsKey("log") && messageBody == $"{deviceId}: valid frame counter, msg: 1 server: 0"),
                "Could not find correct valid frame counter");

            Assert.True(
                await testFixture.EnsureHasEvent((e, deviceIdFromMessage, messageBody) => e.Properties.ContainsKey("log") && messageBody == $"{deviceId}: decoding with: DecoderTemperatureSensor port: 1"),
                "Expecting DecoderTemperatureSensor");

            Assert.True(
                await testFixture.EnsureHasEvent((e, deviceIdFromMessage, messageBody) => e.Properties.ContainsKey("log") && messageBody == $"{deviceId}: sent message '{{\"temperature\": 100}}' to hub"),
                "Expecting message sent in log");


            leafDeviceLog.Clear();
            testFixture.Events.ResetEvents();


            lora.transferPacketWithConfirmed("50", 10);

            // After transferPacketWithConfirmed: Expectation from serial
            // +CMSG: ACK Received
            Assert.Contains("+CMSG: ACK Received", leafDeviceLog);

            // After transferPacketWithConfirmed: Expectation from Log
            // 72AAC86800430020: valid frame counter, msg: 2 server: 1
            // 72AAC86800430020: decoding with: DecoderTemperatureSensor port: 1
            // 72AAC86800430020: sent message '{"temperature": 50}' to hub
            Assert.True(
               await testFixture.EnsureHasEvent((e, deviceIdFromMessage, messageBody) => e.Properties.ContainsKey("log") && messageBody == $"{deviceId}: valid frame counter, msg: 2 server: 1"),
               "Could not find correct valid frame counter");

            Assert.True(
               await testFixture.EnsureHasEvent((e, deviceIdFromMessage, messageBody) => e.Properties.ContainsKey("log") && messageBody == $"{deviceId}: decoding with: DecoderTemperatureSensor port: 1"),
               "Expecting DecoderTemperatureSensor");

            Assert.True(
                await testFixture.EnsureHasEvent((e, deviceIdFromMessage, messageBody) => e.Properties.ContainsKey("log") && messageBody == $"{deviceId}: sent message '{{\"temperature\": 50}}' to hub"),
                "Expecting message sent in log");
         

            //cts.Cancel();
        }
    }
}
