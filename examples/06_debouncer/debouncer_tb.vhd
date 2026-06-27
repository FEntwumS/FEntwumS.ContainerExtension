-- Self-checking testbench for the debouncer.
--
-- The design is instantiated with small generics so that the stability
-- window COUNT_MAX collapses to a handful of cycles, keeping the
-- simulation short while still exercising the bounce-rejection and
-- edge-detection logic. The stimulus is checked against the design
-- contract restated independently as cycle-accurate invariants:
--   1. btn_state ignores bounces shorter than the stability window;
--   2. btn_state asserts only after COUNT_MAX cycles of a stable input,
--      accounting for the two-cycle input synchroniser;
--   3. rising_pulse fires for exactly one cycle on the clean rising edge.

library ieee;
use ieee.std_logic_1164.all;
use ieee.numeric_std.all;
use std.env.finish;

entity debouncer_tb is
end entity debouncer_tb;

architecture sim of debouncer_tb is

    -- CLK_FREQ_HZ / 1_000_000 * STABLE_USEC = 2 * 2 = 4 cycles.
    constant CLK_FREQ_HZ : natural := 2000000;
    constant STABLE_USEC : natural := 2;
    constant COUNT_MAX   : natural := (CLK_FREQ_HZ / 1000000) * STABLE_USEC;

    constant CLK_PERIOD : time := 10 ns;

    signal clk          : std_logic := '0';
    signal rst          : std_logic := '1';
    signal btn_in       : std_logic := '0';
    signal btn_state    : std_logic;
    signal rising_pulse : std_logic;

    -- Counts every rising_pulse strobe; the reference expectation is one
    -- for the single clean rising edge in the stimulus.
    signal pulse_count : natural := 0;

    procedure tick(signal c : out std_logic) is
    begin
        c <= '0';
        wait for CLK_PERIOD / 2;
        c <= '1';
        wait for CLK_PERIOD / 2;
    end procedure;

begin

    dut : entity work.debouncer
        generic map (
            CLK_FREQ_HZ => CLK_FREQ_HZ,
            STABLE_USEC => STABLE_USEC
        )
        port map (
            clk          => clk,
            rst          => rst,
            btn_in       => btn_in,
            btn_state    => btn_state,
            rising_pulse => rising_pulse
        );

    -- Count rising_pulse assertions over the whole run; the rising edge
    -- of the clean stimulus must produce exactly one.
    pulse_counter : process (clk)
    begin
        if rising_edge(clk) then
            if rising_pulse = '1' then
                pulse_count <= pulse_count + 1;
            end if;
        end if;
    end process;

    stimulus : process
    begin
        -- Release reset.
        rst <= '1';
        tick(clk);
        tick(clk);
        rst <= '0';

        -- Idle: output must rest low.
        for i in 0 to 3 loop
            tick(clk);
        end loop;
        assert btn_state = '0'
            report "debouncer_tb: btn_state not low after reset"
            severity failure;

        -- Glitch shorter than the stability window: a single high cycle
        -- followed by a return to low must be rejected entirely.
        btn_in <= '1';
        tick(clk);
        btn_in <= '0';
        for i in 0 to 5 loop
            tick(clk);
            assert btn_state = '0'
                report "debouncer_tb: glitch leaked into btn_state"
                severity failure;
        end loop;

        -- Bouncy then stable press. The bursts below are each shorter
        -- than COUNT_MAX = 4, so none may latch.
        btn_in <= '1'; tick(clk);
        btn_in <= '0'; tick(clk);
        btn_in <= '1'; tick(clk);
        btn_in <= '0'; tick(clk);
        assert btn_state = '0'
            report "debouncer_tb: bounce latched prematurely"
            severity failure;

        -- Now hold high. The input passes through a two-stage
        -- synchroniser (2 cycles) and must then remain stable for
        -- COUNT_MAX cycles before btn_state rises; it must still be low
        -- one cycle before that terminus.
        btn_in <= '1';
        for i in 0 to (2 + COUNT_MAX - 2) loop
            tick(clk);
            assert btn_state = '0'
                report "debouncer_tb: btn_state rose before stability window elapsed"
                severity failure;
        end loop;

        -- One more cycle reaches the terminus and latches the press.
        tick(clk);
        assert btn_state = '1'
            report "debouncer_tb: btn_state failed to rise after stable window"
            severity failure;

        -- Hold longer; no further pulses may be emitted while high.
        for i in 0 to 5 loop
            tick(clk);
            assert btn_state = '1'
                report "debouncer_tb: btn_state dropped while input held high"
                severity failure;
        end loop;

        -- Exactly one rising_pulse over the entire stimulus.
        assert pulse_count = 1
            report "debouncer_tb: rising_pulse fired " & integer'image(pulse_count)
                 & " times, expected 1"
            severity failure;

        report "debouncer_tb: PASS - 4 cases" severity note;
        finish;
    end process;

end architecture sim;
