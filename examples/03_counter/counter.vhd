library ieee;
use ieee.std_logic_1164.all;
use ieee.numeric_std.all;

-- Parametric binary up-counter with synchronous, active-high reset.
-- The count is held in an unsigned register so that wrap-around at 2**WIDTH
-- is defined by numeric_std modular arithmetic rather than left to chance.
entity counter is
  generic (
    WIDTH : natural := 8
  );
  port (
    clk : in  std_logic;
    rst : in  std_logic;
    en  : in  std_logic;
    q   : out std_logic_vector(WIDTH - 1 downto 0)
  );
end entity counter;

architecture rtl of counter is
  signal count : unsigned(WIDTH - 1 downto 0) := (others => '0');
begin

  -- Reset is sampled inside the clocked process; there is no asynchronous
  -- path, so the register infers cleanly on synchronous-reset fabric.
  process (clk)
  begin
    if rising_edge(clk) then
      if rst = '1' then
        count <= (others => '0');
      elsif en = '1' then
        count <= count + 1;
      end if;
    end if;
  end process;

  q <= std_logic_vector(count);

end architecture rtl;
