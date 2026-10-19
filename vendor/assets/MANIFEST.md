# Vendored assets

The gem assets come from the reference app at `90b330024dec3e757c79b6a7e6568f93da8e3148`.
`crates/assets/script/revendor` copied their bytes from the versions in its `Gemfile.lock`.
The table records each gem file and its hash.
`LOAD_PATH` lists Propshaft's search order.
The asset pipeline reads `reference:` entries from the submodule.

The `campfire/` directory contains three application overlays from Rails commit `05c5a2c0d72f7fd74d7c2ace23cc123000f956b9`.
These files retain their source bytes and the Campfire MIT license in `licenses/once-campfire-frontend-MIT-LICENSE`.
Their load path precedes the pinned reference's application JavaScript.

| Application overlay | SHA-256 |
|---|---|
| `controllers/rooms_list_controller.js` | `87c5d6ca13810ed033ea56f1a328f06afa3736b978960ced24b692a57fe07488` |
| `controllers/read_rooms_controller.js` | `9df29819041d919daaa2db8348b3553eb88905fd65dc433f152f22c5c716a4e9` |
| `lib/autocomplete/base_autocomplete_handler.js` | `cea202361b483970f4cfbb6891e429364ee1cff450bc9fb22b6b43197443e553` |

| Gem | Version | Source | Path | SHA-256 |
|---|---|---|---|---|
| lexxy | 0.9.24 | rubygems | app/assets/javascript/lexxy.js | c7e93882639ec38eb4afec8be83340c3a198219ef13c71ccfc22f6368be75c23 |
| lexxy | 0.9.24 | rubygems | app/assets/javascript/lexxy.js.br | cab4870ec9e88e25020e4ea831638e3262d1424cb8304beb57f959a794187e7e |
| lexxy | 0.9.24 | rubygems | app/assets/javascript/lexxy.js.gz | 5f26b0b88fabb261aba7f787613f8834f9b0150cd915ba31f683a41144fd8d2a |
| lexxy | 0.9.24 | rubygems | app/assets/javascript/lexxy.js.map | 13f7e7317722f5217682688c6f55f4cfd196aacab075076901fe80f89bb122d0 |
| lexxy | 0.9.24 | rubygems | app/assets/javascript/lexxy.min.js | bf4edd6ad01d08fa56ecf5d4f59bb5b68a7acd76ea02f9f12d055010125948ed |
| lexxy | 0.9.24 | rubygems | app/assets/javascript/lexxy.min.js.br | f9a166168d9c3ad36209a62ee6ba0379d39b8fce907334913563afb433c7d8df |
| lexxy | 0.9.24 | rubygems | app/assets/javascript/lexxy.min.js.gz | bb8201f6746ae40862ae2e70012dc0ea387db145ba961515493d43043727acfe |
| lexxy | 0.9.24 | rubygems | app/assets/stylesheets/lexxy-content.css | c27c7563c9a70fee6f579f2e574d029537bb9e25e2d1c7ed0f0926bd88f7e8a8 |
| lexxy | 0.9.24 | rubygems | app/assets/stylesheets/lexxy-editor.css | 20e5f800a9393f9b7b1d4b58e7ae396df2ac95eebd7734092196654f79b6c038 |
| lexxy | 0.9.24 | rubygems | app/assets/stylesheets/lexxy-variables.css | c36ad5343cb0286bf3c6213804551300898c37fc3e5f284032fd97287387d91c |
| lexxy | 0.9.24 | rubygems | app/assets/stylesheets/lexxy.css | 340cd4805763af49bc44a8547fa4fec7b284a55b8aee45b2c8016f95f0a5a478 |
| stimulus-rails | 1.3.4 | rubygems | app/assets/javascripts/stimulus-autoloader.js | 55b50b7e09e5929940c07181b24735fb2c1c1edd87ea311e4db679ae5462169b |
| stimulus-rails | 1.3.4 | rubygems | app/assets/javascripts/stimulus-importmap-autoloader.js | 7796430c6a502d3969a405d910de004ed31c2ca80548d801eff1826edfdee1a2 |
| stimulus-rails | 1.3.4 | rubygems | app/assets/javascripts/stimulus-loading.js | 6480747bf3025ea2ac4780cb4e4dda3c98b5d74911d7a07ce66c211f3247e59c |
| stimulus-rails | 1.3.4 | rubygems | app/assets/javascripts/stimulus.js | 08bb1e36a8e1107f07e9e2d59ed57ec734c25fda501f20a37f71883674348659 |
| stimulus-rails | 1.3.4 | rubygems | app/assets/javascripts/stimulus.min.js | 0acd633353c5a12959e474aad56300cab93554eed5d8033d02de800701ca7d59 |
| stimulus-rails | 1.3.4 | rubygems | app/assets/javascripts/stimulus.min.js.map | 3d0f7c81c69552e842a7a822e8ba2b65e9f9b1a72f2faf1e29a4a9e351a555c5 |
| turbo-rails | 2.0.16 | https://github.com/hotwired/turbo-rails.git@30cd8fcc6f82c1ad4edd1ed6069ba878f21f02b3 | app/assets/javascripts/turbo.js | 94351e8f0aadf161b9efd5e60c577d674d25ee99d7b176f74211681b2ac1e708 |
| turbo-rails | 2.0.16 | https://github.com/hotwired/turbo-rails.git@30cd8fcc6f82c1ad4edd1ed6069ba878f21f02b3 | app/assets/javascripts/turbo.min.js | c396e45f669d6e626057902c230746955ab149163133eae179fd5f33c1c8a59b |
| turbo-rails | 2.0.16 | https://github.com/hotwired/turbo-rails.git@30cd8fcc6f82c1ad4edd1ed6069ba878f21f02b3 | app/assets/javascripts/turbo.min.js.map | 8180599d0823b28066883092c3ff151cd5755cfca170a4bdd79d5120ad6e5b21 |
| actiontext | 8.2.0.alpha | https://github.com/rails/rails.git@1a02651ac37fb64b4de2a2b73461d86acf9c98fb | app/assets/javascripts/actiontext.esm.js | 9573353c535ea6d824b0af1a860ee42773200f004b72370c919af2cbd215d852 |
| actiontext | 8.2.0.alpha | https://github.com/rails/rails.git@1a02651ac37fb64b4de2a2b73461d86acf9c98fb | app/assets/javascripts/actiontext.js | 2367b2c686d18f0e600d43f8e01b23b7e342a2911bae953fd400141c02ab7c1d |
| action_text-trix | 2.1.19 | rubygems | app/assets/javascripts/trix.js | 676299911f4cbd15d35b0e12904a12ed1d7cff634502b9bff8518aecace5a9a7 |
| action_text-trix | 2.1.19 | rubygems | app/assets/stylesheets/trix.css | 7a35d25d0204a594f9f89a6fef18f74321ee2dae3970da3ac11e22c4305536ac |
| actioncable | 8.2.0.alpha | https://github.com/rails/rails.git@1a02651ac37fb64b4de2a2b73461d86acf9c98fb | app/assets/javascripts/action_cable.js | 112297e3f1f766d2f385ef5f3c3f9c9068f72be615184c22193c6b7b0b1bc6f0 |
| actioncable | 8.2.0.alpha | https://github.com/rails/rails.git@1a02651ac37fb64b4de2a2b73461d86acf9c98fb | app/assets/javascripts/actioncable.esm.js | 555679e44f119be7a7e54121fca0beb0a5832f2933c1a1edcd0515bbafad22e0 |
| actioncable | 8.2.0.alpha | https://github.com/rails/rails.git@1a02651ac37fb64b4de2a2b73461d86acf9c98fb | app/assets/javascripts/actioncable.js | 1a239ff96644bc8fd626f87a74175b4ee7eafd201f5034034a433c22d8c4dc3e |
| activestorage | 8.2.0.alpha | https://github.com/rails/rails.git@1a02651ac37fb64b4de2a2b73461d86acf9c98fb | app/assets/javascripts/activestorage.esm.js | 30f138a8f0017ae30774450974d16be8af848d6b14014def8dc59aa70ff6998d |
| activestorage | 8.2.0.alpha | https://github.com/rails/rails.git@1a02651ac37fb64b4de2a2b73461d86acf9c98fb | app/assets/javascripts/activestorage.js | 561e6e0d24999cfecaef66dc7b815b72fa3353d9387251aa8ce6b1f5d565c815 |
| actionview | 8.2.0.alpha | https://github.com/rails/rails.git@1a02651ac37fb64b4de2a2b73461d86acf9c98fb | app/assets/javascripts/rails-ujs.esm.js | b8583d8851cb8087e51985f5cd734d176d58b0627bf4c0cd72697e0686a0d985 |
| actionview | 8.2.0.alpha | https://github.com/rails/rails.git@1a02651ac37fb64b4de2a2b73461d86acf9c98fb | app/assets/javascripts/rails-ujs.js | 89f7d2d055b9be3575363cdb34282f5086f09d8de24085c22378baee7499da67 |
