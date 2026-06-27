library ieee;
use ieee.std_logic_1164.all;
use ieee.numeric_std.all;
use std.env.finish;

-- Self-checking testbench for the parametric counter at WIDTH = 4.
-- The reference is an independent software model maintained in this process;
-- the DUT output is compared against it on every cycle, including the
-- 15 -> 0 wrap, so a divergence in either reset, enable gating, or modular
-- arithmetic is caught immediately.
entity counter_tb is
end entity counter_tb;

architecture sim of counter_tb is

  constant WIDTH      : natural := 4;
  constant CLK_PERIOD : time    := 10 ns;

  signal clk : std_logic := '0';
  signal rst : std_logic := '1';
  signal en  : std_logic := '0';
  signal q   : std_logic_vector(WIDTH - 1 downto 0);

  signal clk_en : boolean := true;

  -- Independent reference model of the expected count.
  signal ref_count : unsigned(WIDTH - 1 downto 0) := (others => '0');

  signal checks : natural := 0;

begin

  dut : entity work.counter
    generic map (
      WIDTH => WIDTH
    )
    port map (
      clk => clk,
      rst => rst,
      en  => en,
      q   => q
    );

  -- Free-running clock, gated so the process terminates with the stimulus.
  clk <= not clk after CLK_PERIOD / 2 when clk_en else '0';

  -- Reference advances under the same rules as the DUT and on the same edge,
  -- so both observe identical rst/en at each rising edge.
  process (clk)
  begin
    if rising_edge(clk) then
      if rst = '1' then
        ref_count <= (others => '0');
      elsif en = '1' then
        ref_count <= ref_count + 1;
      end if;
    end if;
  end process;

  stimulus : process
    -- Sample shortly before the next rising edge, after combinational
    -- settling, to compare the registered outputs of DUT and reference.
    procedure check is
    begin
      wait until rising_edge(clk);
      wait for CLK_PERIOD / 4;
      assert q = std_logic_vector(ref_count)
        report "counter: mismatch q=" & integer'image(to_integer(unsigned(q))) &
               " expected=" & integer'image(to_integer(ref_count))
        severity failure;
      checks <= checks + 1;
    end procedure;
  begin
    -- Hold reset for two cycles, enable deasserted.
    check;
    check;

    -- Release reset, enable counting. Sweep the full range and observe the
    -- wrap 15 -> 0; one extra cycle past the wrap confirms the rollover value.
    rst <= '0';
    en  <= '1';
    for i in 0 to 2 ** WIDTH loop
      check;
    end loop;

    -- Enable gating: with en = '0' the count must hold across several cycles.
    en <= '0';
    for i in 0 to 3 loop
      check;
    end loop;

    -- Synchronous reset while enabled forces the count back to zero.
    rst <= '1';
    en  <= '1';
    check;
    rst <= '0';
    check;

    report "counter: PASS - " & integer'image(checks) & " cases"
      severity note;

    clk_en <= false;
    finish;
  end process;

end architecture sim;
