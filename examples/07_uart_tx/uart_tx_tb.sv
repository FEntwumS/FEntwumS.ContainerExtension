`default_nettype none
`timescale 1ns / 1ps

// Self-checking testbench for uart_tx. Parameters are overridden so that
// CLKS_PER_BIT = 1000/100 = 10, keeping the simulation short. The frame is
// sampled at the centre of each bit period (the point a real receiver would
// strobe), reconstructed, and checked field by field against the reference
// byte and the 8N1 framing rules.
module uart_tx_tb;

    localparam integer CLK_FREQ     = 1000;
    localparam integer BAUD         = 100;
    localparam integer CLKS_PER_BIT = CLK_FREQ / BAUD;
    localparam integer CLK_PERIOD   = 10;   // ns; arbitrary, fixes the time base

    localparam [7:0] TEST_BYTE = 8'hB3;     // 1011_0011, asymmetric to catch bit-order errors

    reg        clk = 1'b0;
    reg        rst = 1'b1;
    reg        start = 1'b0;
    reg  [7:0] data = 8'd0;
    wire       tx;
    wire       busy;

    integer    errors = 0;

    uart_tx #(.CLK_FREQ(CLK_FREQ), .BAUD(BAUD)) dut (
        .clk   (clk),
        .rst   (rst),
        .start (start),
        .data  (data),
        .tx    (tx),
        .busy  (busy)
    );

    always #(CLK_PERIOD/2) clk = ~clk;

    // Advance to the centre of the next bit period. The transmitter updates tx
    // on the clock edge that closes a bit; sampling mid-period avoids that edge.
    task automatic sample_bit(output reg b);
        begin
            repeat (CLKS_PER_BIT) @(posedge clk);
            b = tx;
        end
    endtask

    reg        start_bit;
    reg [7:0]  rx;
    reg        stop_bit;
    integer    i;

    initial begin
        repeat (4) @(posedge clk);
        rst <= 1'b0;
        @(posedge clk);

        if (tx !== 1'b1) begin
            $error("uart_tx: line not idle high before transmission (tx=%b)", tx);
            errors = errors + 1;
        end

        data  <= TEST_BYTE;
        start <= 1'b1;
        @(posedge clk);
        start <= 1'b0;

        // The start bit asserts on the same edge that latches `start`; align the
        // sampling phase to the middle of that first bit.
        repeat (CLKS_PER_BIT/2) @(posedge clk);
        start_bit = tx;

        for (i = 0; i < 8; i = i + 1)
            sample_bit(rx[i]);              // LSB-first

        sample_bit(stop_bit);

        if (start_bit !== 1'b0) begin
            $error("uart_tx: start bit = %b, expected 0", start_bit);
            errors = errors + 1;
        end
        if (rx !== TEST_BYTE) begin
            $error("uart_tx: payload = 0x%02h, expected 0x%02h", rx, TEST_BYTE);
            errors = errors + 1;
        end
        if (stop_bit !== 1'b1) begin
            $error("uart_tx: stop bit = %b, expected 1", stop_bit);
            errors = errors + 1;
        end

        // busy must deassert once the frame completes.
        wait (busy == 1'b0);
        @(posedge clk);
        if (tx !== 1'b1) begin
            $error("uart_tx: line not returned to idle high after frame (tx=%b)", tx);
            errors = errors + 1;
        end

        if (errors != 0)
            $fatal(1, "uart_tx: FAIL - %0d error(s)", errors);

        $display("uart_tx: PASS - start/8 data/stop verified for 0x%02h", TEST_BYTE);
        $finish;
    end

    // Guard against a stuck FSM holding the simulation open indefinitely.
    initial begin
        #(CLK_PERIOD * CLKS_PER_BIT * 40);
        $fatal(1, "uart_tx: FAIL - timeout, frame did not complete");
    end

endmodule

`default_nettype wire
