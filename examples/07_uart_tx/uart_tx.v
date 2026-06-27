`default_nettype none

// 8N1 UART transmitter. The frame is one start bit (0), eight data bits sent
// LSB-first, and one stop bit (1); the line idles high. CLKS_PER_BIT derives
// the bit period from the system clock so the module is reusable across clock
// and baud-rate combinations. A registered output avoids glitches on tx.
module uart_tx #(
    parameter integer CLK_FREQ = 1000000,
    parameter integer BAUD     = 115200
) (
    input  wire       clk,
    input  wire       rst,        // synchronous, active high
    input  wire       start,      // pulse to latch data and begin a frame
    input  wire [7:0] data,
    output reg        tx,
    output reg        busy
);

    localparam integer CLKS_PER_BIT = CLK_FREQ / BAUD;

    // Width sized to count a full bit period. $clog2(CLKS_PER_BIT) cannot
    // represent the terminal value CLKS_PER_BIT-1 when CLKS_PER_BIT is a power
    // of two, hence the +1 guard.
    localparam integer CW = (CLKS_PER_BIT <= 1) ? 1 : $clog2(CLKS_PER_BIT) + 1;

    localparam [1:0] IDLE  = 2'd0,
                     START = 2'd1,
                     DATA  = 2'd2,
                     STOP  = 2'd3;

    reg [1:0]      state;
    reg [CW-1:0]   clk_cnt;     // counts system clocks within one bit period
    reg [2:0]      bit_idx;     // 0..7, index of the data bit in flight
    reg [7:0]      shreg;       // captured payload, shifted out LSB-first

    wire bit_done = (clk_cnt == CLKS_PER_BIT[CW-1:0] - 1'b1);

    always @(posedge clk) begin
        if (rst) begin
            state   <= IDLE;
            tx      <= 1'b1;
            busy    <= 1'b0;
            clk_cnt <= {CW{1'b0}};
            bit_idx <= 3'd0;
            shreg   <= 8'd0;
        end else begin
            case (state)
                IDLE: begin
                    tx   <= 1'b1;
                    busy <= 1'b0;
                    if (start) begin
                        shreg   <= data;
                        busy    <= 1'b1;
                        clk_cnt <= {CW{1'b0}};
                        tx      <= 1'b0;       // assert start bit immediately
                        state   <= START;
                    end
                end

                START: begin
                    if (bit_done) begin
                        clk_cnt <= {CW{1'b0}};
                        bit_idx <= 3'd0;
                        tx      <= shreg[0];
                        state   <= DATA;
                    end else begin
                        clk_cnt <= clk_cnt + 1'b1;
                    end
                end

                DATA: begin
                    if (bit_done) begin
                        clk_cnt <= {CW{1'b0}};
                        if (bit_idx == 3'd7) begin
                            tx    <= 1'b1;     // stop bit
                            state <= STOP;
                        end else begin
                            bit_idx <= bit_idx + 1'b1;
                            tx      <= shreg[bit_idx + 1'b1];
                        end
                    end else begin
                        clk_cnt <= clk_cnt + 1'b1;
                    end
                end

                STOP: begin
                    if (bit_done) begin
                        clk_cnt <= {CW{1'b0}};
                        busy    <= 1'b0;
                        state   <= IDLE;
                    end else begin
                        clk_cnt <= clk_cnt + 1'b1;
                    end
                end

                default: state <= IDLE;
            endcase
        end
    end

endmodule

`default_nettype wire
