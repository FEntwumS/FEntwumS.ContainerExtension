-- Single-bit full adder. Purely combinational; the carry expression is the
-- majority function of the three inputs, kept in canonical sum-of-products form
-- so the relationship to the Boolean specification stays explicit for synthesis.
library ieee;
use ieee.std_logic_1164.all;

entity full_adder is
    port (
        a    : in  std_logic;
        b    : in  std_logic;
        cin  : in  std_logic;
        sum  : out std_logic;
        cout : out std_logic
    );
end entity full_adder;

architecture rtl of full_adder is
begin
    sum  <= a xor b xor cin;
    cout <= (a and b) or (a and cin) or (b and cin);
end architecture rtl;
