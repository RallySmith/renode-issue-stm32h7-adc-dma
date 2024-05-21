// Derived from locally patched F2/F4 model original derived from 1.14.0 STM32_ADC.cs
// See STM32_ADC_Fixed.cs for earlier F2/F4 fixes and extensions.
//
// Modifications Copyright (c) 2023-2024 eCosCentric Ltd
// Original assignment for STM32_ADC_cs:
//
// Copyright (c) 2010-2023 Antmicro
//
//  This file is licensed under the MIT License.
//  Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Time;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;

// RM0468 STM32H7[23]3 STM32H77[23]5 STM32H730 : ADC1/2 is MSv62479V2 ADC3 is MSv63820V3
// RM0433 STM32H742    STM32H7[45]3  STM32H750 : ADC1/2/3 are MSv62479V2 (with ADC3 instantiated seperately from ADC1/2)

// Not STM32H7 but:
// RM0033 STM32F2[01]x                         : ADC1/2/3 are single ai16046
// RM0091 STM32F0x[128]                        : ADC single MS30333V3
// RM0368 STM32F401x[BCDE]                     : ADC1 single MS32670V1

namespace Antmicro.Renode.Peripherals.Analog
{
    // Instead of having support for multiple controllers with a single
    // class, we just have this just instantiate a 0x100 wide controller
    // and pass in a Common reg IPeripheral reference That way we can
    // instantiate the common object as needed and reference it when
    // creating controllers.  We could then subsequently provide (if
    // needed) the mechanisms for ADC1 master ADC2 subservient control.
    public class STM32H7_ADC : BasicDoubleWordPeripheral, IWordPeripheral, IBytePeripheral, IKnownSize, IGPIOReceiver
    {
        // NOTE: We do not yet make use of the "model" identifier, but
        // we could use it to suppoprt more than one STM32 variant in
        // this source. At the moment we just simulate the STM32H7[23]
        // RM0468 implementation for ADC1/2
        public STM32H7_ADC(Machine machine, int numberOfChannels = 20, uint maxResolution = 16, STM32H7_ADC_Common commonPeripheral = null, string model = "MSv62479V2") : base(machine)
        {
            if(null == commonPeripheral)
            {
                throw new ConstructionException($"Unspecified STM32H7_ADC_Common peripheral");
            }

            this.common = commonPeripheral;

            NumberOfChannels = numberOfChannels;

            channels = Enumerable.Range(0, NumberOfChannels).Select(x => new ADCChannel(this, x)).ToArray();

            regularSequence = new IValueRegisterField[NumberOfChannels];

            DefineRegisters();

            // Sampling time fixed
            samplingTimer = new LimitTimer(
                machine.ClockSource, 1000000, this, "samplingClock",
                limit: 100,
                eventEnabled: true,
                direction: Direction.Ascending,
                enabled: false,
                autoUpdate: false,
                workMode: WorkMode.OneShot);
            samplingTimer.LimitReached += OnConversionFinished;
        }

        public override void Reset()
        {
            common.Reset();
            foreach(var ch in channels)
            {
                ch.Reset();
            }
            base.Reset();
            //currentChannelIdx = 0;
        }

        public void OnGPIO(int number, bool value)
        {
            this.Log(LogLevel.Debug, "OnGPIO: number {0} value {1} extEn {2} extSel {3}", number, value, extEn.Value, extSel.Value);
            if (ExtEn.Disabled != extEn.Value)
            {
                // number 0..31 for EXTSEL sources
                if (number == (int)extSel.Value)
                {
                    bool doConversion = false;
                    switch (extEn.Value)
                    {
                    case ExtEn.EdgeRising:
                        doConversion = value;
                        break;
                    case ExtEn.EdgeFalling:
                        doConversion = !value;
                        break;
                    case ExtEn.EdgeBoth:
                        doConversion = true;
                        break;
                    }
                    if (doConversion)
                    {
                        this.Log(LogLevel.Debug, "OnGPIO: triggering conversion: regularSequenceLen {0}", regularSequenceLen);
                        // Trigger sampling across ALL the active channels
                        for (uint ach =  0; (ach < regularSequenceLen); ach++)
                        {
                            OnConversionFinished();
                        }
                    }
                }
                else // CONSIDER: adding JEXTSEL sources for number 32..63
                {
                    this.Log(LogLevel.Warning, "OnGPIO: Ignoring external trigger {0} {1} since extSel {2} configured", number, value, (int)extSel.Value);
                }
            }
            else
            {
                this.Log(LogLevel.Warning, "OnGPIO: Ignoring external trigger {0} {1} since extEn disabled", number, value);
            }
        }

        // Even though 8- and 16-bit reads are acceptable for all
        // registers we only model the data register for the moment:
        public byte ReadByte(long offset)
        {
            byte rval = 0;
            if((Registers)offset == Registers.ADC_DR)
            {
                uint drval = ReadDoubleWord(offset);
                rval = (byte)(drval & 0xFF);
            }
            else
            {
                this.LogUnhandledRead(offset);
            }
            this.Log(LogLevel.Debug, "ReadByte: offset 0x{0:X} rval 0x{1:X}", offset, rval);
            return rval;
        }

        public void WriteByte(long offset, byte value)
        {
            this.Log(LogLevel.Debug, "WriteByte: offset 0x{0:X} value 0x{1:X}", offset, value);
            this.LogUnhandledWrite(offset, value);
        }

        public ushort ReadWord(long offset)
        {
            ushort rval = 0;
            if((Registers)offset == Registers.ADC_DR)
            {
                uint drval = ReadDoubleWord(offset);
                rval = (ushort)(drval & 0xFFFF);
            }
            else
            {
                this.LogUnhandledRead(offset);
            }
            this.Log(LogLevel.Debug, "ReadWord: offset 0x{0:X} rval 0x{1:X}", offset, rval);
            return rval;
        }

        public void WriteWord(long offset, ushort value)
        {
            this.Log(LogLevel.Debug, "WriteWord: offset 0x{0:X} value 0x{1:X}", offset, value);
            this.LogUnhandledWrite(offset, value);
        }

        public void FeedSample(uint value, uint channelIdx, int repeat = 1)
        {
            if(IsValidChannel(channelIdx))
            {
                this.Log(LogLevel.Debug, "FeedSample: single value 0x{0:X} channelIdx {1} repeat {2}", value, channelIdx, repeat);
                channels[channelIdx].FeedSample(value, repeat);
            }
        }

        public void FeedSample(string path, uint channelIdx, int repeat = 1)
        {
            if(IsValidChannel(channelIdx))
            {
                var parsedSamples = ADCChannel.ParseSamplesFile(path);
                channels[channelIdx].FeedSample(parsedSamples, repeat);
            }
        }

        private void DefineRegisters()
        {
            Registers.ADC_ISR.Define(this)
                .WithFlag(0, out adcReady, FieldMode.WriteOneToClear, name: "ADRDY (ADC ready)")
                .WithFlag(1, out endOfSampling, name: "EOSMP (Regular channel end of sampling)")
                .WithFlag(2, out endOfConversion, name: "EOC (Regular channel end of conversion)")
                .WithTaggedFlag("EOS (End of regular sequence)", 3)
                .WithTaggedFlag("OVR (ADC overrun)", 4)
                .WithTaggedFlag("JEOC (Injected channel end of conversion)", 5)
                .WithTaggedFlag("JEOS (Injected channel end of sequence)", 6)
                .WithTaggedFlag("AWD1 (Analog watchdog 1)", 7)
                .WithTaggedFlag("AWD2 (Analog watchdog 2)", 8)
                .WithTaggedFlag("AWD3 (Analog watchdog 3)", 9)
                .WithTaggedFlag("JQOVF (Injected context queue overflow)", 10)
                .WithReservedBits(11, 1)
                .WithFlag(12, name: "LDORDY")
                .WithReservedBits(13, 19);

            Registers.ADC_IER.Define(this)
                .WithTaggedFlag("ADRDYIE", 0)
                .WithTaggedFlag("EOSMPIE", 1)
                .WithFlag(2, out eocInterruptEnable, name: "EOCIE")
                .WithTaggedFlag("EOSIE", 3)
                .WithTaggedFlag("OVRIE", 4)
                .WithTaggedFlag("JEOCIE", 5)
                .WithTaggedFlag("JEOSIE", 6)
                .WithTaggedFlag("AWD1IE", 7)
                .WithTaggedFlag("AWD2IE", 8)
                .WithTaggedFlag("AWD3IE", 9)
                .WithTaggedFlag("JQOVFIE", 10)
                .WithReservedBits(11, 21);

            Registers.ADC_CR.Define(this, 0x20000000, name: "ADC_CR")
                .WithFlag(0, out adcOn, name: "ADEN (ADC Enable)",
                          changeCallback: (_, val) => { if(val) { EnableADC(); } else { DisableADC(); }})
                .WithFlag(1, FieldMode.WriteOneToClear, name: "ADDIS (ADC Disable)",
		          changeCallback: (_, val) => { if(val) { DisableADC(); adcOn.Value = false; }})
                .WithFlag(2, out scanMode, name: "ADSTART") // only allowed to set when ADEN==1 and ADDIS==0
                .WithFlag(3, name: "JADSTART") // only allowed to set when ADEN==1 and ADDIS==0
                .WithFlag(4, FieldMode.WriteOneToClear, name: "ADSTP") // auto-cleared by hardware
                .WithFlag(5, name: "JADSTP")
                .WithReservedBits(6, 2)
                .WithEnumField(8, 2, out boost, name: "BOOST")
                .WithReservedBits(10, 6)
                .WithFlag(16, name: "ADCALLIN")
                .WithReservedBits(17, 5)
                .WithFlag(22, name: "LINCALRDYW1")
                .WithFlag(23, name: "LINCALRDYW2")
                .WithFlag(24, name: "LINCALRDYW3")
                .WithFlag(25, name: "LINCALRDYW4")
                .WithFlag(26, name: "LINCALRDYW5")
                .WithFlag(27, name: "LINCALRDYW6")
                .WithFlag(28, name: "ADVREGEN")
                .WithFlag(29, name: "DEEPPWD")
                .WithFlag(30, name: "ADCALDIF")
                .WithFlag(31, FieldMode.Read | FieldMode.WriteOneToClear, name: "ADCAL"); // set by S/W to start calibration // cleared by H/W

            Registers.ADC_CFGR.Define(this, 0x80000000, name: "ADC_CFGR")
                .WithEnumField(0, 2, out dataManagement, name: "DMNGT",
                               changeCallback: (_, val) => { UpdateDMAConfig(); })
                .WithValueField(2, 3, out dataResolution, name: "RES") // write only when ADSTART=0 and JADSTART=0
                .WithEnumField(5, 5, out extSel, name: "EXTSEL (External event select for regular group)")
                .WithEnumField(10, 2, out extEn, name: "EXTEN (External trigger enable for regular channels)",
                               writeCallback: (_, value) => { UpdateExtEn(); })
                .WithTaggedFlag("OVRMOD", 12)
                .WithFlag(13, out continuousConversion, name: "CONT (Continous conversion)")
                .WithTaggedFlag("AUTDLY", 14)
                .WithReservedBits(15, 1)
                .WithTaggedFlag("DISCEN", 16)
                .WithValueField(17, 3, out discontinuousModeChannelCount, name: "DISCNUM")
                .WithTaggedFlag("JDISCEN", 20)
                .WithTaggedFlag("JQM", 21)
                .WithTaggedFlag("AWD1SGL", 22)
                .WithTaggedFlag("AWD1EN", 23)
                .WithTaggedFlag("JAWD1EN", 24)
                .WithTaggedFlag("JAUTO", 25)
                .WithValueField(26, 5, name: "AWD1CH")
                .WithTaggedFlag("JQDIS", 31);

            Registers.ADC_CFGR2.Define(this)
                .WithTaggedFlag("ROVSE", 0)
                .WithTaggedFlag("JOVSE", 1)
                .WithReservedBits(2, 3)
                .WithValueField(5, 4, name: "OVSS")
                .WithTaggedFlag("TROVS", 9)
                .WithTaggedFlag("ROVSM", 10)
                .WithTaggedFlag("RSHIFT1", 11)
                .WithTaggedFlag("RSHIFT2", 12)
                .WithTaggedFlag("RSHIFT3", 13)
                .WithTaggedFlag("RSHIFT4", 14)
                .WithReservedBits(15, 1)
                .WithValueField(16, 10, name: "OSVR")
                .WithReservedBits(26, 2)
                .WithValueField(28, 4, name: "LSHIFT");

            Registers.ADC_SMPR1.Define(this)
                .WithTag("SMP0 (Channel 0 sampling time)", 0, 3)
                .WithTag("SMP1 (Channel 1 sampling time)", 3, 3)
                .WithTag("SMP2 (Channel 2 sampling time)", 6, 3)
                .WithTag("SMP3 (Channel 3 sampling time)", 9, 3)
                .WithTag("SMP4 (Channel 4 sampling time)", 12, 3)
                .WithTag("SMP5 (Channel 5 sampling time)", 15, 3)
                .WithTag("SMP6 (Channel 6 sampling time)", 18, 3)
                .WithTag("SMP7 (Channel 7 sampling time)", 21, 3)
                .WithTag("SMP8 (Channel 8 sampling time)", 24, 3)
                .WithTag("SMP9 (Channel 9 sampling time)", 27, 3)
                .WithReservedBits(30, 2);

            Registers.ADC_SMPR2.Define(this)
                .WithTag("SMP10 (Channel 10 sampling time)", 0, 3)
                .WithTag("SMP11 (Channel 11 sampling time)", 3, 3)
                .WithTag("SMP12 (Channel 12 sampling time)", 6, 3)
                .WithTag("SMP13 (Channel 13 sampling time)", 9, 3)
                .WithTag("SMP14 (Channel 14 sampling time)", 12, 3)
                .WithTag("SMP15 (Channel 15 sampling time)", 15, 3)
                .WithTag("SMP16 (Channel 16 sampling time)", 18, 3)
                .WithTag("SMP17 (Channel 17 sampling time)", 21, 3)
                .WithTag("SMP18 (Channel 18 sampling time)", 24, 3)
                .WithTag("SMP19 (Channel 19 sampling time)", 27, 3)
                .WithReservedBits(30, 2);

            Registers.ADC_PCSEL.Define(this)
                .WithValueField(0, NumberOfChannels, name: "PCSEL")
                .WithReservedBits(NumberOfChannels, (32 - NumberOfChannels));

            Registers.ADC_LTR1.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_HTR1.Define(this, 0x03FFFFFF)
                .WithReservedBits(0, 32);

            Registers.ADC_SQR1.Define(this)
                .WithValueField(0, 4, writeCallback: (_, val) => { regularSequenceLen = (uint)val + 1; }, name: "L (Regular channel sequence length)")
                .WithValueField(6, 5, out regularSequence[0], name: "SQ1 (1st conversion in regular sequence)")
                .WithValueField(12, 5, out regularSequence[1], name: "SQ2 (2nd conversion in regular sequence)")
                .WithValueField(18, 5, out regularSequence[2], name: "SQ3 (3rd conversion in regular sequence)")
                .WithValueField(24, 5, out regularSequence[3], name: "SQ4 (4th conversion in regular sequence)")
                .WithReservedBits(29, 3);

            Registers.ADC_SQR2.Define(this)
                .WithValueField(0, 5, out regularSequence[4], name: "SQ5 (5th conversion in regular sequence)")
                .WithValueField(6, 5, out regularSequence[5], name: "SQ6 (6th conversion in regular sequence)")
                .WithValueField(12, 5, out regularSequence[6], name: "SQ7 (7th conversion in regular sequence)")
                .WithValueField(18, 5, out regularSequence[7], name: "SQ8 (8th conversion in regular sequence)")
                .WithValueField(24, 5, out regularSequence[8], name: "SQ9 (9th conversion in regular sequence)")
                .WithReservedBits(29, 3);

            Registers.ADC_SQR3.Define(this)
                .WithValueField(0, 5, out regularSequence[9], name: "SQ10 (10th conversion in regular sequence)")
                .WithValueField(6, 5, out regularSequence[10], name: "SQ11 (11th conversion in regular sequence)")
                .WithValueField(12, 5, out regularSequence[11], name: "SQ12 (12th conversion in regular sequence)")
                .WithValueField(18, 5, out regularSequence[12], name: "SQ13 (13th conversion in regular sequence)")
                .WithValueField(24, 5, out regularSequence[13], name: "SQ14 (14th conversion in regular sequence)")
                .WithReservedBits(29, 3);

            Registers.ADC_SQR4.Define(this)
                .WithValueField(0, 5, out regularSequence[14], name: "SQ15 (15th conversion in regular sequence)")
                .WithValueField(6, 5, out regularSequence[15], name: "SQ16 (16th conversion in regular sequence)")
                .WithReservedBits(11, 21);

            Registers.ADC_DR.Define(this)
                .WithValueField(0, 32,
                                valueProviderCallback: _ =>
                                {
                                    this.Log(LogLevel.Debug, "Reading ADC data {0}", adcData);
                                    // Reading ADC_DR should clear EOC
                                    endOfConversion.Value = false;
                                    IRQ.Set(false);
                                    return adcData;
                                });

            Registers.ADC_JSQR.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_OFR1.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_OFR2.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_OFR3.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_OFR4.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_JDR1.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_JDR2.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_JDR3.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_JDR4.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_AWD2CR.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_AWD3CR.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_LTR2.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_HTR2.Define(this, 0x03FFFFFF)
                .WithReservedBits(0, 32);

            Registers.ADC_LTR3.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_HTR3.Define(this, 0x03FFFFFF)
                .WithReservedBits(0, 32);

            Registers.ADC_DIFSEL.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_CALFACT.Define(this)
                .WithReservedBits(0, 32);

            Registers.ADC_CALFACT2.Define(this)
                .WithReservedBits(0, 32);
        }

        private bool IsValidChannel(uint channelIdx)
        {
            if(channelIdx >= NumberOfChannels)
            {
                throw new RecoverableException("Only channels 0/1 are supported");
            }
            return true;
        }

        public long Size => 0x100;

        public GPIO IRQ { get; } = new GPIO();
        public GPIO DMARequest { get; } = new GPIO();

        public int NumberOfChannels { get; }

        private void EnableADC()
        {
            adcReady.Value = true;
            currentChannel = channels[regularSequence[currentChannelIdx].Value];
            this.Log(LogLevel.Debug, "EnableADC: currentChannelIdx {0} channel {1}", currentChannelIdx, regularSequence[currentChannelIdx].Value);
            if (scanMode.Value && !samplingTimer.Enabled)
            {
                StartConversion(); // really a restart
            }
        }

        private void DisableADC()
        {
            this.Log(LogLevel.Debug, "DisableADC: setting currentChannelIdx = 0");
            currentChannelIdx = 0;
            // NOTE: RM0033 rev9 10.3.1 : we should stop conversion if ADC disabled

            // If the internal timer is enabled then
            // OnConversionFinished is repeatedly called from the
            // samplingTimer LimitReached support; (samplingTimer is not
            // needed when we are using an EXTSEL trigger for
            // conversion).

            samplingTimer.Enabled = false;
            currentChannel = null;
        }

        private void UpdateDMAConfig()
        {
            this.Log(LogLevel.Debug, "UpdateDMAConfig: dataManagement {0}", dataManagement);
            switch ((DataManagement)dataManagement.Value)
            {
            case DataManagement.DMAOneShot:
            case DataManagement.DMACircular:
                dmaEnabled = true;
                break;
            default:
                dmaEnabled = false;
                break;
            }
            this.Log(LogLevel.Debug, "UpdateDMAConfig: dmaEnabled {0}", dmaEnabled);
        }

        private void StartConversion()
        {
            if(adcOn.Value)
            {
                this.Log(LogLevel.Debug, "Starting conversion time={0}",
                         machine.ElapsedVirtualTime.TimeElapsed);

                if (ExtEn.Disabled == extEn.Value)
                {
                    // Enable timer, which will simulate conversion being performed.
                    samplingTimer.Enabled = true;
                }
            }
            else
            {
                this.Log(LogLevel.Warning, "Trying to start conversion while ADC off");
            }
        }

        private void OnConversionFinished()
        {
            this.Log(LogLevel.Debug, "OnConversionFinished: time={0} currentChannelIdx {1} regularSequenceLen {2} channel {3}",
                     machine.ElapsedVirtualTime.TimeElapsed,
                     currentChannelIdx, regularSequenceLen, regularSequence[currentChannelIdx].Value);

            // Extra diagnostics to make tracking enabled channels easier:
            for (var rs = 0; (rs < regularSequenceLen); rs++)
            {
                this.Log(LogLevel.Debug, "OnConversionFinished: regularSequence[{0}] {1}", rs, regularSequence[rs].Value);
            }

            if (null == currentChannel)
            {
                this.Log(LogLevel.Debug, "OnConversionFinished: early exit since currentChannel == null");
                return;
            }

            // Set data register and trigger DMA request
            currentChannel.PrepareSample();
            adcData = currentChannel.GetSample();

            this.Log(LogLevel.Debug, "OnConversionFinished:DBG: adcData=0x{0:X} dmaEnabled {1}", adcData, dmaEnabled);

            if(dmaEnabled)
            {
                // Issue DMA peripheral request, which when mapped to DMA
                // controller will trigger a peripheral to memory transfer
                DMARequest.Set();
                DMARequest.Unset();
            }

            this.Log(LogLevel.Debug, "OnConversionFinished:DBG: regularSequenceLen={0} scanMode {1} currentChannelIdx {2}", regularSequenceLen, scanMode.Value, currentChannelIdx);

            var scanModeActive = scanMode.Value && currentChannelIdx <= regularSequenceLen - 1;
            var scanModeFinished = scanMode.Value && currentChannelIdx == regularSequenceLen - 1;

            this.Log(LogLevel.Debug, "OnConversionFinished:DBG: scanModeActive={0} scanModeFinished={1}", scanModeActive, scanModeFinished);

            // Signal EOC if EOCS set with scan mode enabled and finished or we finished scanning regular group
            endOfConversion.Value = scanModeActive ? scanModeFinished : true;

            this.Log(LogLevel.Debug, "OnConversionFinished:DBG: endOfConversion.Value={0}", endOfConversion.Value);

            if(0 != regularSequenceLen)
            {
                // Iterate to next channel
                currentChannelIdx = (currentChannelIdx + 1) % regularSequenceLen;
                currentChannel = channels[regularSequence[currentChannelIdx].Value];
            }
            // currentChannel channelId is private so we cannot easily verify the correct channel
            this.Log(LogLevel.Debug, "OnConversionFinished:DBG: currentChannelIdx={0} channel={1} continuousConversion {2}", currentChannelIdx,
                     regularSequence[currentChannelIdx].Value,
                     continuousConversion.Value);

            // Auto trigger next conversion if we're scanning or CONT bit set
            if (ExtEn.Disabled == extEn.Value)
            {
                samplingTimer.Enabled = scanModeActive || continuousConversion.Value;
            }

            // Trigger EOC interrupt
            if(endOfConversion.Value && eocInterruptEnable.Value)
            {
                this.Log(LogLevel.Debug, "OnConversionFinished: Set IRQ");
                IRQ.Set(true);
            }
        }

        private void UpdateExtEn()
        {
            this.Log(LogLevel.Debug, "UpdateExtEn: extEn {0}", extEn.Value);
            if (ExtEn.Disabled == extEn.Value)
            {
                if (scanMode.Value && !samplingTimer.Enabled)
                {
                    this.Log(LogLevel.Debug, "UpdateExtEn: enabling internal timer");
                    samplingTimer.Enabled = true;
                }
            }
            else
            {
                samplingTimer.Enabled = false;
            }
        }

        private IFlagRegisterField scanMode; // ADC_CR:ADSTART
        private IFlagRegisterField endOfConversion;
        private IFlagRegisterField adcOn;
        private IFlagRegisterField eocInterruptEnable;
        private IFlagRegisterField continuousConversion;

        private IFlagRegisterField adcReady;
        private IFlagRegisterField endOfSampling;
        private IEnumRegisterField<Boost> boost;
        private IEnumRegisterField<DataManagement> dataManagement;
        private IValueRegisterField dataResolution; // TODO: 0==16 5=14 6=12 3=10 7=8 // TODO: provide as enum
        private IValueRegisterField discontinuousModeChannelCount; // TODO: reg value + 1 // #-regular-channels to be converted on receiving external trigger

        private bool dmaEnabled;

        private IEnumRegisterField<ExtSel> extSel;
        private IEnumRegisterField<ExtEn> extEn;

        // Data sample to be returned from data register when read.
        private uint adcData;

        // Regular sequence settings, i.e. the channels and order of channels
        // for performing conversion
        private uint regularSequenceLen;
        private readonly IValueRegisterField[] regularSequence;

        // Channel objects, for managing input test data
        private uint currentChannelIdx; // index into regularSequence[] vector
        private ADCChannel currentChannel;
        private readonly ADCChannel[] channels;

        private readonly STM32H7_ADC_Common common;

        // Sampling timer. Provides time-based event for driving conversion of
        // regular channel sequence.
        private readonly LimitTimer samplingTimer;

        private enum ExtSel
        {
            TIM1_CH1   =  0,
            TIM1_CH2   =  1,
            TIM1_CH3   =  2,
            TIM2_CH2   =  3,
            TIM3_TRGO  =  4,
            TIM4_CH4   =  5,
            EXTI_11    =  6,
            TIM8_TRGO  =  7,
            TIM8_TRGO2 =  8,
            TIM1_TRGO  =  9,
            TIM1_TRGO2 = 10,
            TIM2_TRGO  = 11,
            TIM4_TRGO  = 12,
            TIM6_TRGO  = 13,
            TIM15_TRGO = 14,
            TIM3_CH4   = 15,
            RSVD16     = 16,
            RSVD17     = 17,
            LPTIM1_OUT = 18,
            LPTIM2_OUT = 19,
            LPTIM3_OUT = 20,
            TIM23_TRGO = 21,
            TIM24_TRGO = 22,
            RSVD23     = 23,
            RSVD24     = 24,
            RSVD25     = 25,
            RSVD26     = 26,
            RSVD27     = 27,
            RSVD28     = 28,
            RSVD29     = 29,
            RSVD30     = 30,
            RSVD31     = 31,
        }

        private enum ExtEn
        {
            Disabled    = 0, // Trigger detection disabled
            EdgeRising  = 1, // Detection on the rising edge
            EdgeFalling = 2, // Detection on the falling edge
            EdgeBoth    = 3, // Detection on both the rising and falling edges
        }

        // ADC_CR:BOOST
        private enum Boost
        {
            To6p25MHz = 0, // used when ADCclock <= 6.25MHz
            To12p5MHz = 1, // used when 6.25MHz < ADCclock <= 12.5MHz
            To25MHz = 2,   // used when 12.5MHz < ADCclock <= 25MHz
            To50MHz = 3,   // used when 25MHz < ADCclock <= 50MHz
        }

        // ADC_CFGR:DMNGT
        private enum DataManagement
        {
            RegularDR = 0,
            DMAOneShot = 1,
            DFSDM = 2,
            DMACircular = 3,
        }

        // ADCx (MSv62479V2) registers
        private enum Registers
        {
            ADC_ISR = 0x00,
            ADC_IER = 0x04,
            ADC_CR = 0x08,
            ADC_CFGR = 0x0C,
            ADC_CFGR2 = 0x10,
            ADC_SMPR1 = 0x14,
            ADC_SMPR2 = 0x18,
            ADC_PCSEL = 0x1C,
            ADC_LTR1 = 0x20,
            ADC_HTR1 = 0x24,
            ADC_SQR1 = 0x30,
            ADC_SQR2 = 0x34,
            ADC_SQR3 = 0x38,
            ADC_SQR4 = 0x3C,
            ADC_DR = 0x40,
            ADC_JSQR = 0x4C,
            ADC_OFR1 = 0x60,
            ADC_OFR2 = 0x64,
            ADC_OFR3 = 0x68,
            ADC_OFR4 = 0x6C,
            ADC_JDR1 = 0x80,
            ADC_JDR2 = 0x84,
            ADC_JDR3 = 0x88,
            ADC_JDR4 = 0x8C,
            ADC_AWD2CR = 0xA0,
            ADC_AWD3CR = 0xA4,
            ADC_LTR2 = 0xB0,
            ADC_HTR2 = 0xB4,
            ADC_LTR3 = 0xB8,
            ADC_HTR3 = 0xBC,
            ADC_DIFSEL = 0xC0,
            ADC_CALFACT = 0xC4,
            ADC_CALFACT2 = 0xC8
        }
    }

    //-------------------------------------------------------------------------

    public class STM32H7_ADC_Common : BasicDoubleWordPeripheral, IWordPeripheral, IBytePeripheral, IKnownSize
    {
        // TODO: parent controller "model" to distinguish different
        // RM0468 ADC3 register offsets. For the moment we are passing
        // "ccrOnly"
        public STM32H7_ADC_Common(Machine machine, bool ccrOnly = false) : base(machine)
        {
            this.ccrOnly = ccrOnly;
            // TODO:CONSIDER: link back to ADC controller // so maybe we define the ADC1 first and reference here as master; and then ADC2 would go through ADC1 to find the common parent?
        }

        public long Size => 0x100;

        // Common registers
        //
        //  common @ 0x300 shared between ADC1/2 with RM0433 explicit that ADC3 is seperate
        //              RM0468 Rev3                                     RM0433 Rev7             RM0033 Rev9
        //      offset  ADC1@0x000      ADC2@0x100      ADC3@0x000      same as RM0468 ADC1/2   ADC1/2/3
        //      ------  ----------      ----------      ----------      ---------------------   ----------
        //     300      ADCx_CSR        ADCx_CSR        -                                       ADC_CSR
        //     304      -               -               -                                       ADC_CCR
        //     308      ADCx_CCR        ADCx_CCR        ADC_CCR                                 ADC_CDR
        //     30C      ADCx_CDR        ADCx_CDR        -
        //     310      ADCx_CDR2       ADCx_CDR2       -

        public override void Reset()
        {
            // TODO
        }

        public byte ReadByte(long offset)
        {
            byte rval = 0x00;
            // TODO
            this.Log(LogLevel.Debug, "ReadByte: offset 0x{0:X} rval 0x{1:X}", offset, rval);
            return rval;
        }

        public ushort ReadWord(long offset)
        {
            ushort rval = 0x0000;
            // TODO
            this.Log(LogLevel.Debug, "ReadWord: offset 0x{0:X} rval 0x{1:X}", offset, rval);
            return rval;
        }

        public void WriteByte(long offset, byte value)
        {
            this.Log(LogLevel.Debug, "WriteByte: offset 0x{0:X} value 0x{1:X}", offset, value);
            this.LogUnhandledWrite(offset, value);
        }

        public void WriteWord(long offset, ushort value)
        {
            this.Log(LogLevel.Debug, "WriteWord: offset 0x{0:X} value 0x{1:X}", offset, value);
            this.LogUnhandledWrite(offset, value);
        }

        private bool ccrOnly;
    }
}

// EOF STM32H7_ADC.cs
