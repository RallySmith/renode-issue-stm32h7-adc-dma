# STM32H7 ADC timer driven DMA

The provided `ecos_nucleo144_stm32h723_io_adc1` executes on the
NUCLEO-H723ZG hardware platform using DMA driven ADC, where the DMA is
triggered by a configured timer (TRGO).

Currently this is a **mirror** (and not a fork) of the
renode-issue-reproduction-template because github limits users to a
single fork.

Due to the interconnected nature of the H/W controller configuration
more than one model needs to be updated to correctly simulate the
required functionality.

This fork **will** eventually provide updated models as required to
successully execute the test. The initial commit is against an
unmodified Renode environment to exhibit the missing features.

The `fixed` branch provides the new models:

| Model                                     | Description
|:------------------------------------------|:---------------------------------
| `platforms/peripherals/STM32H7_ADC.cs`    | Derived from earlier F2/F4 model
| `platforms/peripherals/STM32H7_DMAMUX.cs` | Initial implementation for ADC DMA
| `platforms/peripherals/STM32H7_PWR.cs`    | Derived from earlier STM32_PWR.cs

As per the original Renode world and its models, they are not yet 100%
simulations, but do provide enough functionality for common DMA, Timer
and ADC use for timer-driven DMA transferred ADC sampling.

**NOTE** The `platforms/peripherals/STM32_Timer_Fixed.cs` and
`platforms/peripherals/STM32DMA_Fixed.cs` are the same as the
https://github.com/RallySmith/renode-issue-stm32f2-adc-dma `fixed`
branch.


## branches

| Branch  | Description
|:--------|:-------------------------------------------------------------------
| `main`  | test fails to complete against latest and stable renode worlds
| `fixed` | updated models to allow successful execution of the application
