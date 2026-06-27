-- Synchronous FIFO with a circular buffer and an explicit occupancy counter.
--
-- A separate occupancy register (rather than the classic pointer-MSB scheme)
-- is used because it yields full/empty flags that are combinationally exact
-- for any DEPTH and trivially generalises the count output. Writes asserted
-- while full and reads asserted while empty are silently ignored, so the
-- pointers can never overrun one another.

library ieee;
use ieee.std_logic_1164.all;
use ieee.numeric_std.all;
use ieee.math_real.all;  -- ceil/log2 for elaboration-time width computation

entity sync_fifo is
    generic (
        DATA_WIDTH : natural := 8;
        DEPTH      : natural := 16  -- must be a power of two
    );
    port (
        clk   : in  std_logic;
        rst   : in  std_logic;  -- synchronous, active high
        wr_en : in  std_logic;
        rd_en : in  std_logic;
        din   : in  std_logic_vector(DATA_WIDTH - 1 downto 0);
        dout  : out std_logic_vector(DATA_WIDTH - 1 downto 0);
        full  : out std_logic;
        empty : out std_logic;
        -- One extra bit so the count range [0, DEPTH] is representable.
        count : out std_logic_vector(integer(ceil(log2(real(DEPTH)))) downto 0)
    );
end entity sync_fifo;

architecture rtl of sync_fifo is

    -- Address width for DEPTH entries; pointers wrap naturally modulo DEPTH
    -- since DEPTH is a power of two.
    constant ADDR_W : natural := integer(ceil(log2(real(DEPTH))));

    type mem_t is array (0 to DEPTH - 1) of std_logic_vector(DATA_WIDTH - 1 downto 0);
    signal mem : mem_t;

    signal wr_ptr : unsigned(ADDR_W - 1 downto 0) := (others => '0');
    signal rd_ptr : unsigned(ADDR_W - 1 downto 0) := (others => '0');
    -- Occupancy spans 0..DEPTH inclusive, hence ADDR_W + 1 bits.
    signal occ    : unsigned(ADDR_W downto 0) := (others => '0');

    signal full_i  : std_logic;
    signal empty_i : std_logic;

begin

    full_i  <= '1' when occ = to_unsigned(DEPTH, occ'length) else '0';
    empty_i <= '1' when occ = 0 else '0';

    -- Effective enables after the full/empty interlock. A simultaneous
    -- read and write on a non-empty, non-full FIFO leaves occupancy
    -- unchanged, which the counter update below handles without a special case.
    process (clk)
        variable do_wr : boolean;
        variable do_rd : boolean;
    begin
        if rising_edge(clk) then
            if rst = '1' then
                wr_ptr <= (others => '0');
                rd_ptr <= (others => '0');
                occ    <= (others => '0');
            else
                do_wr := (wr_en = '1') and (full_i = '0');
                do_rd := (rd_en = '1') and (empty_i = '0');

                if do_wr then
                    mem(to_integer(wr_ptr)) <= din;
                    wr_ptr <= wr_ptr + 1;
                end if;

                if do_rd then
                    rd_ptr <= rd_ptr + 1;
                end if;

                if do_wr and not do_rd then
                    occ <= occ + 1;
                elsif do_rd and not do_wr then
                    occ <= occ - 1;
                end if;
            end if;
        end if;
    end process;

    -- First-word-fall-through is not required by the brief; dout is the
    -- registered read location and is valid the cycle after rd_en.
    dout  <= mem(to_integer(rd_ptr));
    full  <= full_i;
    empty <= empty_i;
    count <= std_logic_vector(occ);

end architecture rtl;
