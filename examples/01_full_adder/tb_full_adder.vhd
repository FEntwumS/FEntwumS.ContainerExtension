-- Exhaustive self-checking testbench for full_adder. The reference model sums
-- the three input bits as integers; the low bit of that sum is the expected
-- sum output and the high bit the expected carry, which is independent of the
-- DUT's gate-level structure.
library ieee;
use ieee.std_logic_1164.all;
use ieee.numeric_std.all;
use std.env.finish;

entity tb_full_adder is
end entity tb_full_adder;

architecture sim of tb_full_adder is
    signal a, b, cin   : std_logic := '0';
    signal sum, cout   : std_logic;

    -- Settling delay between applying a vector and sampling the outputs.
    constant t_settle : time := 10 ns;

    function to_sl(v : integer) return std_logic is
    begin
        if v = 0 then
            return '0';
        else
            return '1';
        end if;
    end function;
begin
    dut : entity work.full_adder
        port map (
            a    => a,
            b    => b,
            cin  => cin,
            sum  => sum,
            cout => cout
        );

    stimulus : process
        variable ref_sum  : integer;
        variable exp_sum  : std_logic;
        variable exp_cout : std_logic;
    begin
        for i in 0 to 7 loop
            a   <= to_sl((i / 4) mod 2);
            b   <= to_sl((i / 2) mod 2);
            cin <= to_sl(i mod 2);
            wait for t_settle;

            ref_sum  := (i / 4) mod 2 + (i / 2) mod 2 + i mod 2;
            exp_sum  := to_sl(ref_sum mod 2);
            exp_cout := to_sl(ref_sum / 2);

            assert sum = exp_sum
                report "sum mismatch for vector " & integer'image(i) &
                       ": got " & std_logic'image(sum) &
                       ", expected " & std_logic'image(exp_sum)
                severity failure;
            assert cout = exp_cout
                report "cout mismatch for vector " & integer'image(i) &
                       ": got " & std_logic'image(cout) &
                       ", expected " & std_logic'image(exp_cout)
                severity failure;
        end loop;

        report "tb_full_adder: PASS - 8 cases" severity note;
        finish;
    end process stimulus;
end architecture sim;
