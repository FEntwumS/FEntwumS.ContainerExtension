-- Seven-segment hexadecimal decoder for a common-anode display.
--
-- Segments are active-LOW: a driven '0' lights the corresponding LED, since the
-- common anode is tied high and each segment cathode sinks current when pulled
-- low. The output vector packs the segments as seg = g f e d c b a, i.e. seg(0)
-- is segment a and seg(6) is segment g. The lookup encodes the canonical hex
-- font (0..9, A..F) using the lower-case b and d conventions to disambiguate
-- glyphs that would otherwise collide with 8 and 0.

library ieee;
use ieee.std_logic_1164.all;
use ieee.numeric_std.all;

entity seven_seg_decoder is
    port (
        digit : in  std_logic_vector(3 downto 0);
        seg   : out std_logic_vector(6 downto 0)
    );
end entity seven_seg_decoder;

architecture rtl of seven_seg_decoder is
begin
    -- Combinational decode. The constants below are the active-LOW segment
    -- patterns in g-f-e-d-c-b-a order; a '0' bit lights a segment.
    with digit select seg <=
        "1000000" when "0000",  -- 0: a b c d e f
        "1111001" when "0001",  -- 1: b c
        "0100100" when "0010",  -- 2: a b d e g
        "0110000" when "0011",  -- 3: a b c d g
        "0011001" when "0100",  -- 4: b c f g
        "0010010" when "0101",  -- 5: a c d f g
        "0000010" when "0110",  -- 6: a c d e f g
        "1111000" when "0111",  -- 7: a b c
        "0000000" when "1000",  -- 8: all segments
        "0010000" when "1001",  -- 9: a b c d f g
        "0001000" when "1010",  -- A: a b c e f g
        "0000011" when "1011",  -- b: c d e f g
        "1000110" when "1100",  -- C: a d e f
        "0100001" when "1101",  -- d: b c d e g
        "0000110" when "1110",  -- E: a d e f g
        "0001110" when "1111",  -- F: a e f g
        "1111111" when others;  -- blank guard for metavalues
end architecture rtl;
