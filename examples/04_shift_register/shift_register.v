// Parameterized parallel-load, left-shifting serial shift register.
// Synchronous, active-high reset. Load takes precedence over shift so that a
// concurrent load/shift_en asserts deterministic behaviour (parallel data wins).
`default_nettype none

module shift_register #(
    parameter integer WIDTH = 8
) (
    input  wire             clk,
    input  wire             rst,      // synchronous, active high
    input  wire             load,     // parallel load enable
    input  wire [WIDTH-1:0] d,        // parallel input
    input  wire             sin,      // serial input shifted into LSB
    input  wire             shift_en, // serial shift enable
    output reg  [WIDTH-1:0] q,
    output wire             sout       // serial output from MSB
);

    always @(posedge clk) begin
        if (rst)
            q <= {WIDTH{1'b0}};
        else if (load)
            q <= d;
        else if (shift_en)
            q <= {q[WIDTH-2:0], sin}; // left shift: MSB drops out, sin enters LSB
    end

    assign sout = q[WIDTH-1];

endmodule

`default_nettype wire
