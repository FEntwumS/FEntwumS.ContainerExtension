// Self-checking testbench for mux4. The DUT is exercised at the default width
// and re-instantiated at a narrower width to confirm the parameterization is
// honoured. The reference is the indexed lookup of the driven word vector,
// which is independent of the case statement under test.
`default_nettype none
`timescale 1ns / 1ps

module mux4_tb;

    localparam integer WIDTH = 8;

    reg  [WIDTH-1:0] d [0:3];
    reg  [1:0]       sel;
    wire [WIDTH-1:0] y;

    integer cases = 0;

    mux4 #(.WIDTH(WIDTH)) dut (
        .d0 (d[0]),
        .d1 (d[1]),
        .d2 (d[2]),
        .d3 (d[3]),
        .sel(sel),
        .y  (y)
    );

    // Narrow instance to verify WIDTH propagation rather than a fixed 8-bit path.
    localparam integer NWIDTH = 4;
    reg  [NWIDTH-1:0] nd [0:3];
    reg  [1:0]        nsel;
    wire [NWIDTH-1:0] ny;

    mux4 #(.WIDTH(NWIDTH)) ndut (
        .d0 (nd[0]),
        .d1 (nd[1]),
        .d2 (nd[2]),
        .d3 (nd[3]),
        .sel(nsel),
        .y  (ny)
    );

    integer i;

    initial begin
        // Four distinct words; values chosen to be mutually distinguishable
        // and to populate both halves of the 8-bit field.
        d[0] = 8'hA5;
        d[1] = 8'h3C;
        d[2] = 8'hF0;
        d[3] = 8'h69;

        nd[0] = 4'h1;
        nd[1] = 4'h2;
        nd[2] = 4'h4;
        nd[3] = 4'h8;

        for (i = 0; i < 4; i = i + 1) begin
            sel  = i[1:0];
            nsel = i[1:0];
            #1;
            if (y !== d[i]) begin
                $error("mux4: WIDTH=%0d sel=%0d expected %h got %h", WIDTH, i, d[i], y);
                $fatal(1);
            end
            if (ny !== nd[i]) begin
                $error("mux4: WIDTH=%0d sel=%0d expected %h got %h", NWIDTH, i, nd[i], ny);
                $fatal(1);
            end
            cases = cases + 2;
        end

        $display("mux4: PASS - %0d cases", cases);
        $finish;
    end

endmodule

`default_nettype wire
