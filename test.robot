*** Settings ***
Suite Setup                   Setup
Suite Teardown                Teardown
Test Setup                    Reset Emulation
Test Teardown                 Test Teardown
Test Timeout                  20 seconds
Resource                      ${RENODEKEYWORDS}

*** Variables ***
${SCRIPT}                     ${CURDIR}/test.resc
${UART}                       sysbus.usart3


*** Keywords ***
Load Script
    Execute Script            ${SCRIPT}
    Create Terminal Tester    ${UART}
    Create Log Tester         1


*** Test Cases ***
Should Run Test Case
    Load Script
    Start Emulation
    Should Not Be In Log        sysbus: [cpu: 0x80016F0] ReadDoubleWord from non existing peripheral at 0x58024818.
    INFO:<ADC test>