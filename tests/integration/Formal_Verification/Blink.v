module Blink (
    input clk,
    output led
);

reg [23:0] counter = 0;

always @ (posedge clk) begin
    counter <= counter + 1'b1;
end

assign led = counter[20];

`ifdef FORMAL
    // Formal properties
    always @(posedge clk) begin
        // The counter should not overflow within the BMC depth
        assert (counter < 24'hFFFFFF);
    end
`endif

endmodule
