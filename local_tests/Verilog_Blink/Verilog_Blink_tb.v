module Verilog_Blink_tb;
    reg clk;
    wire led;

    Verilog_Blink uut (
        .clk(clk),
        .led(led)
    );

    initial begin
        clk = 0;
        // Run for 1000 time units
        #1000;
        $finish;
    end

    always #5 clk = ~clk;
endmodule
