// Self-checking testbench for shift_register.
// An independent behavioural reference is updated with identical stimulus and
// the DUT is compared against it on every clock edge. The serial path is
// checked end to end: a known pattern is loaded, then clocked out of sout while
// a second pattern is clocked in through sin, after which q must equal the
// shifted-in word.
`default_nettype none
`timescale 1ns/1ps

module shift_register_tb;

    localparam integer WIDTH = 8;

    reg              clk = 1'b0;
    reg              rst;
    reg              load;
    reg  [WIDTH-1:0] d;
    reg              sin;
    reg              shift_en;
    wire [WIDTH-1:0] q;
    wire             sout;

    // Independent reference state, evolved by the same update rule the design
    // is specified to obey (load precedence over shift, MSB-out / LSB-in).
    reg  [WIDTH-1:0] ref_q;

    integer checks = 0;
    integer i;

    shift_register #(.WIDTH(WIDTH)) dut (
        .clk(clk), .rst(rst), .load(load), .d(d),
        .sin(sin), .shift_en(shift_en), .q(q), .sout(sout)
    );

    always #5 clk = ~clk;

    // Reference advances on the same edge as the DUT.
    always @(posedge clk) begin
        if (rst)
            ref_q <= {WIDTH{1'b0}};
        else if (load)
            ref_q <= d;
        else if (shift_en)
            ref_q <= {ref_q[WIDTH-2:0], sin};
    end

    // Sample after the edge has settled and confirm DUT tracks the reference.
    task check;
        begin
            #1;
            if (q !== ref_q) begin
                $error("q mismatch at t=%0t: dut=%b ref=%b", $time, q, ref_q);
                $fatal(1);
            end
            if (sout !== ref_q[WIDTH-1]) begin
                $error("sout mismatch at t=%0t: dut=%b ref=%b", $time, sout, ref_q[WIDTH-1]);
                $fatal(1);
            end
            checks = checks + 1;
        end
    endtask

    initial begin
        rst = 1'b1; load = 1'b0; d = '0; sin = 1'b0; shift_en = 1'b0;
        ref_q = '0;
        @(posedge clk); check; // reset clears

        rst = 1'b0;

        // Parallel load a known pattern.
        d = 8'hA5; load = 1'b1;
        @(posedge clk); load = 1'b0; check;

        // Idle: neither load nor shift; state must hold.
        @(posedge clk); check;

        // Serial sequence: drain the loaded word out of sout MSB-first while a
        // second word enters through sin LSB-last. After WIDTH shifts q equals
        // the freshly shifted-in pattern.
        begin : serial_seq
            reg [WIDTH-1:0] inject;
            inject = 8'h3C;
            shift_en = 1'b1;
            for (i = 0; i < WIDTH; i = i + 1) begin
                sin = inject[WIDTH-1-i]; // feed MSB-first so q holds inject after WIDTH shifts
                @(posedge clk); check;
            end
            shift_en = 1'b0;
            sin = 1'b0;
            if (q !== 8'h3C) begin
                $error("serial shift-in result wrong: got %b expected %b", q, 8'h3C);
                $fatal(1);
            end
        end

        // Load precedence: assert load and shift_en together; load must win.
        d = 8'hF0; load = 1'b1; shift_en = 1'b1; sin = 1'b1;
        @(posedge clk); load = 1'b0; shift_en = 1'b0; sin = 1'b0; check;

        // Mid-stream synchronous reset overrides everything.
        rst = 1'b1; shift_en = 1'b1; sin = 1'b1;
        @(posedge clk); rst = 1'b0; shift_en = 1'b0; sin = 1'b0; check;

        $display("shift_register: PASS - %0d cases", checks);
        $finish;
    end

    // Watchdog: bound simulation independent of stimulus correctness.
    initial begin
        #10000;
        $error("timeout: testbench did not finish");
        $fatal(1);
    end

endmodule

`default_nettype wire
