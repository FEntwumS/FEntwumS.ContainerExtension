#include "VVerilog_Blink.h"
#include "verilated.h"
#include <memory>

int main(int argc, char** argv) {
    const std::unique_ptr<VerilatedContext> contextp{new VerilatedContext};
    contextp->commandArgs(argc, argv);
    const std::unique_ptr<VVerilog_Blink> top{new VVerilog_Blink{contextp.get(), "TOP"}};
    
    top->clk = 0;
    while (!contextp->gotFinish() && contextp->time() < 100) {
        contextp->timeInc(1);
        top->clk = !top->clk;
        top->eval();
    }
    return 0;
}
