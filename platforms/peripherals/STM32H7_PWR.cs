// Derived from 1.14.0 STM32_PWR.cs
//
// Modifications Copyright (c) 2024 eCosCentric Ltd
// Original assignments:
//
// Copyright (c) 2010-2023 Antmicro
// Copyright (c) 2022 SICK AG
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Exceptions;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public sealed class STM32H7_PWR : BasicDoubleWordPeripheral, IKnownSize
    {
        public STM32H7_PWR(string stm32Family, Machine machine) : base(machine)
        {
            if(null == stm32Family)
            {
                throw new ConstructionException("stm32Family was null");
            }
            // "H72"/"H73" RM0468
            // "H74"/"H75" RM0433
            this.Model = stm32Family;
            DefineRegisters();
        }
        
        private void DefineRegisters()
        {
            Registers.PowerControl1.Define(this, 0xF000C000, name: "PWR_CR1")
                .WithTaggedFlag("LPDS", 0)
                .WithReservedBits(1, 3)
                .WithTaggedFlag("PVDE", 4)
                .WithEnumField<DoubleWordRegister, PvdLevelSelection>(5, 3, name: "PLS")
                .WithFlag(8, out disableBackupProtection, name: "DBP", changeCallback:(_, value) => { AccessRTC(value); })
                .WithTaggedFlag("FLPS", 9)
                .WithReservedBits(10, 4)
                .WithEnumField<DoubleWordRegister, StopVoltageScalingOutputSelection>(14, 2, out stopvosValue, name: "SVOS", writeCallback: (_, value) =>
                    {
                        if(value == StopVoltageScalingOutputSelection.Reserved)
                        {
                            stopvosValue.Value = StopVoltageScalingOutputSelection.ScaleMode3;
                        }
                    })
                .WithTaggedFlag("AVDEN", 16)
                .WithEnumField<DoubleWordRegister, AnalogLevelSelection>(17, 2, name: "ALS")
                .WithReservedBits(19, 13);
                
            Registers.PowerControlStatus.Define(this, 0x00004000, name: "PWR_CSR1")
                .WithReservedBits(0, 4)
                .WithTaggedFlag("PVDO", 4)
                .WithReservedBits(5, 8)
                //.WithTaggedFlag("ACTVOSRDY", 13) // return 1 to indicate ready
		.WithFlag(13, FieldMode.Read, valueProviderCallback: _ => true, name: "ACTVOSRDY") // always indicate ready
                .WithEnumField<DoubleWordRegister, RegulatorVoltageScalingOutputSelection>(14, 2, out actvosValue, FieldMode.Read, name: "ACTVOS")
                .WithTaggedFlag("AVDO", 16)
                .WithReservedBits(17, 15);

            Registers.PowerControl2.Define(this, name: "PWR_CR2")
		.WithTaggedFlag("BREN", 0)
		.WithReservedBits(1, 3)
		.WithTaggedFlag("MONEN", 4)
		.WithReservedBits(5, 11)
		.WithTaggedFlag("BRRDY", 16)
		.WithReservedBits(17, 3)
		.WithTaggedFlag("VBATL", 20)
		.WithTaggedFlag("VBATH", 21)
		.WithTaggedFlag("TEMPL", 22)
		.WithTaggedFlag("TEMPL", 23)
		.WithReservedBits(24, 8);

            Registers.PowerControl3.Define(this, 0x00000046, name: "PWR_CR3") // reset 0x00000006 for "H74"/"H75"
		.WithTaggedFlag("BYPASS", 0)
		.WithTaggedFlag("LDOEN", 1)
		.WithTaggedFlag("SDEN", 2)
		.WithTaggedFlag("SDEXTHP", 3) // TODO: "H72"/"H73" only
		.WithValueField(4, 2, name: "SDLEVEL") // TODO: "H72"/"H73" only
		.WithReservedBits(6, 2)
		.WithTaggedFlag("VBE", 8)
		.WithTaggedFlag("VBRS", 9)
		.WithReservedBits(10, 6)
		.WithTaggedFlag("SDEXTRDY", 16) // TODO: "H72"/"H73" only
		.WithReservedBits(17, 7)
		.WithTaggedFlag("USB33DEN", 24)
		.WithTaggedFlag("USBREGEN", 25)
		.WithTaggedFlag("USB33RDY", 26)
		.WithReservedBits(27, 5);

            Registers.PowerControlCPU.Define(this, name: "PWR_CPUCR")
		.WithTaggedFlag("PDDS_D1", 0)
		.WithTaggedFlag("PDDS_D2", 1)
		.WithTaggedFlag("PDDS_D3", 2)
		.WithReservedBits(3, 2)
		.WithTaggedFlag("STOPF", 5)
		.WithTaggedFlag("SBF", 6)
		.WithTaggedFlag("SBF_D1", 7)
		.WithTaggedFlag("SBF_D2", 8)
		.WithTaggedFlag("CSSF", 9)
		.WithReservedBits(10, 1)
		.WithTaggedFlag("RUN_D3", 11)
		.WithReservedBits(12, 20);
		
            Registers.PowerDomainD3.Define(this, 0x00004000, name: "PWR_D3CR") // RM0468 "H72"/"H73" 0x00006000 for subsequent resets
		.WithReservedBits(0, 13)
		//.WithTaggedFlag("VOSRDY", 13)
		.WithFlag(13, FieldMode.Read, valueProviderCallback: _ => true, name: "VOSRDY") // always indicate ready
                .WithEnumField<DoubleWordRegister, RegulatorVoltageScalingOutputSelection>(14, 2, out actvosValue, name: "VOS")
		.WithReservedBits(16, 16);

            Registers.PowerWakeUpClear.Define(this, name: "PWR_WKUPCR") // TODO: read-as-zero, w1c bits
		.WithTaggedFlag("WKUPC1", 0)
		.WithTaggedFlag("WKUPC2", 1)
		.WithTaggedFlag("WKUPC3", 2) // TODO "H74"/"H75" only
		.WithTaggedFlag("WKUPC4", 3)
		.WithTaggedFlag("WKUPC5", 4) // TODO "H74"/"H75" only
		.WithTaggedFlag("WKUPC6", 5)
		.WithReservedBits(6, 26);

            Registers.PowerWakeUpFlags.Define(this, name: "PWR_WKUPFR") // TODO: read-only
		.WithTaggedFlag("WKUPF1", 0)
		.WithTaggedFlag("WKUPF2", 1)
		.WithTaggedFlag("WKUPF3", 2) // TODO "H74"/"H75" only
		.WithTaggedFlag("WKUPF4", 3)
		.WithTaggedFlag("WKUPF5", 4) // TODO "H74"/"H75" only
		.WithTaggedFlag("WKUPF6", 5)
		.WithReservedBits(6, 26);

            Registers.PowerWakeUpEnable.Define(this, name: "PWR_WKUPEPR")
		.WithTaggedFlag("WKUPEN1", 0)
		.WithTaggedFlag("WKUPEN2", 1)
		.WithTaggedFlag("WKUPEN3", 2) // TODO "H74"/"H75" only
		.WithTaggedFlag("WKUPEN4", 3)
		.WithTaggedFlag("WKUPEN5", 4) // TODO "H74"/"H75" only
		.WithTaggedFlag("WKUPEN6", 5)
		.WithReservedBits(6, 2)
		.WithTaggedFlag("WKUPP1", 8)
		.WithTaggedFlag("WKUPP2", 9)
		.WithTaggedFlag("WKUPP3", 10) // TODO "H74"/"H75" only
		.WithTaggedFlag("WKUPP4", 11)
		.WithTaggedFlag("WKUPP5", 12) // TODO "H74"/"H75" only
		.WithTaggedFlag("WKUPP6", 13)
		.WithReservedBits(14, 2)
                .WithEnumField<DoubleWordRegister, WakeUpPullConfiguration>(16, 2, name: "WKUPPUPD1")
                .WithEnumField<DoubleWordRegister, WakeUpPullConfiguration>(18, 2, name: "WKUPPUPD2")
                .WithEnumField<DoubleWordRegister, WakeUpPullConfiguration>(20, 2, name: "WKUPPUPD3") // TODO "H74"/"H75" only
                .WithEnumField<DoubleWordRegister, WakeUpPullConfiguration>(22, 2, name: "WKUPPUPD4")
                .WithEnumField<DoubleWordRegister, WakeUpPullConfiguration>(24, 2, name: "WKUPPUPD5") // TODO "H74"/"H75" only
                .WithEnumField<DoubleWordRegister, WakeUpPullConfiguration>(26, 2, name: "WKUPPUPD6")
		.WithReservedBits(28, 4);
        }

        private void AccessRTC(bool dbp)
        {
            this.Log(LogLevel.Debug, "TODO: AccessRTC: dbp {0}", dbp);
            // TODO:IMPLEMENT: We should control access to the RTC and RTC Backup registers and backup SRAM via a GPIO signal
        }

        public long Size => 0x400;

        public string Model { get; }

        private IEnumRegisterField<StopVoltageScalingOutputSelection> stopvosValue;
        private IEnumRegisterField<RegulatorVoltageScalingOutputSelection> actvosValue;
        private IFlagRegisterField disableBackupProtection;

        private enum Registers
        {
            PowerControl1      = 0x00, // PWR_CR1
            PowerControlStatus = 0x04, // PWR_CSR1
            PowerControl2      = 0x08, // PWR_CR2
            PowerControl3      = 0x0C, // PWR_CR3
            PowerControlCPU    = 0x10, // PWR_CPUCR
            PowerDomainD3      = 0x18, // PWR_D3CR
            PowerWakeUpClear   = 0x20, // PWR_WKUPCR
            PowerWakeUpFlags   = 0x24, // PWR_WKUPFR
            PowerWakeUpEnable  = 0x28  // PWR_WKUPEPR
        }

        private enum PvdLevelSelection
        {
            V1_95,
            V2_10,
            V2_25,
            V2_40,
            V2_55,
            V2_70,
            V2_85,
            VEXT
        }

        private enum AnalogLevelSelection
        {
            V1_7,
            V2_1,
            V2_5,
            V2_8
        }

        private enum RegulatorVoltageScalingOutputSelection
        {
            Scale0,
            Scale3,
            Scale2,
            Scale1
        }

        private enum StopVoltageScalingOutputSelection
        {
            Reserved,
            ScaleMode5,
            ScaleMode4,
            ScaleMode3
        }

        private enum WakeUpPullConfiguration
        {
	    None,
	    PullUp,
	    PullDown,
            Reserved
        }
    }
}
