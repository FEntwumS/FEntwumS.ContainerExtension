-- Self-checking testbench for sync_fifo.
--
-- The reference model is an independent software queue (a fixed array plus
-- head/tail/count tracked in the process). Every observed dout is checked
-- against the value the reference dequeues, decoupling the check from the
-- DUT's own pointer arithmetic. The stimulus deliberately:
--   1. fills the FIFO and asserts that an overflowing write is dropped,
--   2. drains it in FIFO order and asserts data integrity and empty,
--   3. forces pointer wraparound by cycling more than DEPTH elements.

library ieee;
use ieee.std_logic_1164.all;
use ieee.numeric_std.all;
use ieee.math_real.all;
use std.env.finish;

entity sync_fifo_tb is
end entity sync_fifo_tb;

architecture sim of sync_fifo_tb is

    constant DATA_WIDTH : natural := 8;
    constant DEPTH      : natural := 16;
    constant ADDR_W     : natural := integer(ceil(log2(real(DEPTH))));
    constant PERIOD     : time    := 10 ns;

    signal clk   : std_logic := '0';
    signal rst   : std_logic := '1';
    signal wr_en : std_logic := '0';
    signal rd_en : std_logic := '0';
    signal din   : std_logic_vector(DATA_WIDTH - 1 downto 0) := (others => '0');
    signal dout  : std_logic_vector(DATA_WIDTH - 1 downto 0);
    signal full  : std_logic;
    signal empty : std_logic;
    signal count : std_logic_vector(ADDR_W downto 0);

    signal sim_done : boolean := false;

    -- Reference queue, independent of the DUT implementation.
    type ref_arr_t is array (0 to DEPTH - 1) of unsigned(DATA_WIDTH - 1 downto 0);

    -- Deterministic, non-trivial data pattern so position errors are visible.
    function pattern (i : natural) return std_logic_vector is
    begin
        return std_logic_vector(to_unsigned((i * 7 + 3) mod 256, DATA_WIDTH));
    end function;

begin

    dut : entity work.sync_fifo
        generic map (
            DATA_WIDTH => DATA_WIDTH,
            DEPTH      => DEPTH
        )
        port map (
            clk => clk, rst => rst, wr_en => wr_en, rd_en => rd_en,
            din => din, dout => dout, full => full, empty => empty,
            count => count
        );

    clk <= '0' when sim_done else not clk after PERIOD / 2;

    stim : process
        -- Software reference of the data we expect to dequeue, in order.
        variable expq   : ref_arr_t;
        variable q_head : natural := 0;
        variable q_tail : natural := 0;
        variable q_cnt  : natural := 0;
        variable cases  : natural := 0;

        -- Resume a short delay past the rising edge so that the DUT's
        -- registered updates (occ, pointers, dout) have propagated before the
        -- testbench samples any output. Sampling exactly on the edge would race
        -- the clocked process across delta cycles.
        constant SETTLE : time := PERIOD / 4;
        procedure tick is
        begin
            wait until rising_edge(clk);
            wait for SETTLE;
        end procedure;

        -- Drive one write; ref-enqueue only when the FIFO has room.
        procedure push (v : std_logic_vector) is
        begin
            din   <= v;
            wr_en <= '1';
            if q_cnt < DEPTH then
                expq(q_tail) := unsigned(v);
                q_tail := (q_tail + 1) mod DEPTH;
                q_cnt  := q_cnt + 1;
            end if;
            tick;
            wr_en <= '0';
            din   <= (others => '0');
        end procedure;

        -- dout is combinational on rd_ptr, so the head word is already present
        -- before the read fires. Sample it, then pulse rd_en for one cycle to
        -- advance the pointer and dequeue the reference in lockstep.
        procedure pop is
            variable exp : unsigned(DATA_WIDTH - 1 downto 0);
        begin
            assert q_cnt > 0
                report "sync_fifo_tb: reference underflow in stimulus"
                severity failure;
            exp    := expq(q_head);
            q_head := (q_head + 1) mod DEPTH;
            q_cnt  := q_cnt - 1;

            assert unsigned(dout) = exp
                report "sync_fifo_tb: data mismatch, got " &
                       integer'image(to_integer(unsigned(dout))) &
                       " expected " & integer'image(to_integer(exp))
                severity failure;
            cases := cases + 1;

            rd_en <= '1';
            tick;            -- read accepted; rd_ptr advances at this edge
            rd_en <= '0';
        end procedure;

    begin
        -- Release synchronous reset.
        rst <= '1';
        tick;
        rst <= '0';
        tick;
        assert empty = '1' and full = '0'
            report "sync_fifo_tb: not empty after reset" severity failure;
        assert unsigned(count) = 0
            report "sync_fifo_tb: nonzero count after reset" severity failure;
        cases := cases + 1;

        -- Phase 1: fill to full.
        for i in 0 to DEPTH - 1 loop
            push(pattern(i));
        end loop;
        assert full = '1'
            report "sync_fifo_tb: full not asserted at capacity" severity failure;
        assert unsigned(count) = DEPTH
            report "sync_fifo_tb: count /= DEPTH at capacity" severity failure;
        cases := cases + 1;

        -- Overflowing write must be dropped: occupancy and the value that will
        -- later surface at the head must be unaffected. push() does not enqueue
        -- into the reference when q_cnt = DEPTH, so the drain below proves it.
        push(pattern(DEPTH + 99));
        assert full = '1' and unsigned(count) = DEPTH
            report "sync_fifo_tb: overflow write altered occupancy" severity failure;
        cases := cases + 1;

        -- Phase 2: drain to empty in FIFO order, checking each word.
        for i in 0 to DEPTH - 1 loop
            pop;
        end loop;
        assert empty = '1'
            report "sync_fifo_tb: empty not asserted after full drain" severity failure;
        assert unsigned(count) = 0
            report "sync_fifo_tb: count /= 0 after full drain" severity failure;
        cases := cases + 1;

        -- Underflowing read on an empty FIFO must be ignored.
        rd_en <= '1';
        tick;
        rd_en <= '0';
        tick;
        assert empty = '1' and unsigned(count) = 0
            report "sync_fifo_tb: read on empty disturbed state" severity failure;
        cases := cases + 1;

        -- Phase 3: force pointer wraparound. Streaming 3*DEPTH+5 elements with
        -- interleaved single-entry occupancy cycles the pointers past their
        -- modulo-DEPTH boundary several times.
        for i in 0 to 3 * DEPTH + 4 loop
            push(pattern(i + 32));
            pop;
        end loop;
        assert empty = '1'
            report "sync_fifo_tb: not empty after wraparound sequence" severity failure;
        cases := cases + 1;

        report "sync_fifo: PASS - " & integer'image(cases) & " cases" severity note;
        sim_done <= true;
        finish;
        wait;
    end process;

end architecture sim;
