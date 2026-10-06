\ Test
\ Copyright (C) 2026 Ulrich Hoffmann, SPDX-License-Identifier: GPL-3.0-or-later

base @ decimal

: hello ." hi there" ;
: countup ( u -- ) 0 DO I . LOOP ;
: test ( -- )  10 countup ;

base !

cr .( loaded )
