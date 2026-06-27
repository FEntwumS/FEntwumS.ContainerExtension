// 4-to-1 multiplexer, parameterized word width.
// Combinational select; the full case over the 2-bit selector is exhaustive,
// so no latch is inferred and no default branch is required.
`default_nettype none

module mux4 #(
    parameter WIDTH = 8
) (
    input  wire [WIDTH-1:0] d0,
    input  wire [WIDTH-1:0] d1,
    input  wire [WIDTH-1:0] d2,
    input  wire [WIDTH-1:0] d3,
    input  wire [1:0]       sel,
    output reg  [WIDTH-1:0] y
);

    always @* begin
        case (sel)
            2'b00: y = d0;
            2'b01: y = d1;
            2'b10: y = d2;
            2'b11: y = d3;
        endcase
    end

endmodule

`default_nettype wire
