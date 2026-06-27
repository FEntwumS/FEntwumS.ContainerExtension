-- Self-checking testbench for seven_seg_decoder.
--
-- The reference patterns are held in an independent constant array indexed by
-- the integer value of the input code, deliberately authored from the segment
-- geometry rather than copied from the design, so that a transcription error in
-- either file is caught. All sixteen codes are exercised exhaustively.

library ieee;
use ieee.std_logic_1164.all;
use ieee.numeric_std.all;
use std.env.finish;

entity seven_seg_decoder_tb is
end entity seven_seg_decoder_tb;

architecture sim of seven_seg_decoder_tb is

    signal digit : std_logic_vector(3 downto 0);
    signal seg   : std_logic_vector(6 downto 0);

    type seg_rom_t is array (0 to 15) of std_logic_vector(6 downto 0);

    -- Reference font in g-f-e-d-c-b-a order, active LOW.
    constant ref : seg_rom_t := (
        0  => "1000000",
        1  => "1111001",
        2  => "0100100",
        3  => "0110000",
        4  => "0011001",
        5  => "0010010",
        6  => "0000010",
        7  => "1111000",
        8  => "0000000",
        9  => "0010000",
        10 => "0001000",
        11 => "0000011",
        12 => "1000110",
        13 => "0100001",
        14 => "0000110",
        15 => "0001110"
    );

begin

    dut : entity work.seven_seg_decoder
        port map (
            digit => digit,
            seg   => seg
        );

    stimulus : process
    begin
        for code in 0 to 15 loop
            digit <= std_logic_vector(to_unsigned(code, 4));
            wait for 10 ns;
            assert seg = ref(code)
                report "seven_seg_decoder: mismatch at code " & integer'image(code) &
                       " expected " & to_string(ref(code)) &
                       " got "      & to_string(seg)
                severity failure;
        end loop;

        report "seven_seg_decoder: PASS - 16 cases" severity note;
        finish;
    end process stimulus;

end architecture sim;
