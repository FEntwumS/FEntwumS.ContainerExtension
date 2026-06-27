-- Synchronous switch/button debouncer with edge detection.
--
-- The input is first passed through a two-stage synchroniser to remove
-- metastability hazards on a signal that is asynchronous to clk. The
-- synchronised level must then persist for COUNT_MAX consecutive clock
-- cycles before it is accepted as the new stable output, which rejects
-- contact bounce shorter than the STABLE_USEC guard interval. A
-- one-cycle strobe marks every clean 0->1 transition of the debounced
-- output.

library ieee;
use ieee.std_logic_1164.all;
use ieee.numeric_std.all;
use ieee.math_real.all;

entity debouncer is
    generic (
        CLK_FREQ_HZ : natural := 1000000;
        STABLE_USEC : natural := 100
    );
    port (
        clk          : in  std_logic;
        rst          : in  std_logic;
        btn_in       : in  std_logic;
        btn_state    : out std_logic;
        rising_pulse : out std_logic
    );
end entity debouncer;

architecture rtl of debouncer is

    -- Cycles the input must remain stable before it is latched. The
    -- guard is clamped to at least one so that degenerate generics
    -- (sub-microsecond windows) still yield a usable counter terminus.
    constant COUNT_RAW : natural := (CLK_FREQ_HZ / 1000000) * STABLE_USEC;
    constant COUNT_MAX : natural := maximum(1, COUNT_RAW);

    signal sync       : std_logic_vector(1 downto 0) := (others => '0');
    signal sampled    : std_logic;
    signal counter    : unsigned(integer(ceil(log2(real(COUNT_MAX + 1)))) - 1 downto 0);
    signal state_reg  : std_logic := '0';

begin

    sampled <= sync(1);

    process (clk)
    begin
        if rising_edge(clk) then
            if rst = '1' then
                sync         <= (others => '0');
                counter      <= (others => '0');
                state_reg    <= '0';
                rising_pulse <= '0';
            else
                -- Stage 1/2 synchroniser for the asynchronous input.
                sync <= sync(0) & btn_in;

                rising_pulse <= '0';

                if sampled = state_reg then
                    -- Input agrees with the accepted level; no bounce in
                    -- progress, so restart the stability window.
                    counter <= (others => '0');
                else
                    if counter = COUNT_MAX - 1 then
                        state_reg <= sampled;
                        counter   <= (others => '0');
                        -- Strobe only on the clean low-to-high edge.
                        if sampled = '1' then
                            rising_pulse <= '1';
                        end if;
                    else
                        counter <= counter + 1;
                    end if;
                end if;
            end if;
        end if;
    end process;

    btn_state <= state_reg;

end architecture rtl;
