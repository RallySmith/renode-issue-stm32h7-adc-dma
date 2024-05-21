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

## branches

| Branch  | Description
|:--------|:-------------------------------------------------------------------
| `main`  | test fails to complete against latest and stable renode worlds
| `fixed` | updated models to allow successful execution of the application
